# Include the C# ESE source in Sum

Sum includes the core source of the local `EseRecovery` 0.3.0-alpha.1 library and references its project directly, so a checkout can build without an external repository or unpublished NuGet feed. This replaces both the Python backend and Windows ManagedEsent; all platforms use the same bounded recovery implementation through the original single-reader CLI. Future fixes to the standalone library must be deliberately synchronized with this copy; native Windows recovery capabilities beyond this implementation are no longer available in Sum.
