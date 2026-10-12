"""Transport tests; real ESE parsing is covered separately with private samples."""
import base64
import importlib.util
import pathlib
import unittest
from types import SimpleNamespace

path = pathlib.Path(__file__).resolve().parents[2] / "SumData/python/ese_reader.py"
spec = importlib.util.spec_from_file_location("ese_reader", path)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class Record:
    def __init__(self, parsed, raw):
        self.parsed, self.raw = parsed, raw

    def get(self, name, raw=False):
        return self.raw if raw else self.parsed


class TransportTests(unittest.TestCase):
    def test_filetime_does_not_round_through_float_or_datetime(self):
        raw = (133000000001234567).to_bytes(8, "little")
        column = SimpleNamespace(name="LastAccess", is_text=False, ctype=SimpleNamespace(name="DateTime"))
        self.assertEqual(base64.b64decode(helper.encode_value(Record(None, raw), column)), raw)

    def test_text_uses_resolved_long_value_and_utf16(self):
        column = SimpleNamespace(name="ClientName", is_text=True)
        text = "utente è 東京"
        self.assertEqual(base64.b64decode(helper.encode_value(Record(text, b"reference"), column)).decode("utf-16-le"), text)

    def test_binary_resolves_long_value(self):
        column = SimpleNamespace(name="Address", is_text=False, ctype=SimpleNamespace(name="Long Binary"))
        self.assertEqual(base64.b64decode(helper.encode_value(Record(b"\xc0\x00\x02\x01", b"reference"), column)), b"\xc0\x00\x02\x01")

    def test_null_and_empty_remain_distinct(self):
        column = SimpleNamespace(name="ClientName", is_text=True)
        self.assertIsNone(helper.encode_value(Record(None, None), column))
        self.assertEqual(helper.encode_value(Record("", None), column), "")

    def test_multivalue_is_rejected_instead_of_silently_stringified(self):
        with self.assertRaises(ValueError):
            helper.encode_value(Record(["a", "b"], None), SimpleNamespace(name="ClientName", is_text=True))
