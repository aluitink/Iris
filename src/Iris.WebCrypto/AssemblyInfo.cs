using System.Runtime.CompilerServices;

// Expose internals to the dedicated test project so the WebCrypto bridge bootstrap (a process-global
// static) can be reset between tests — see tests/Iris.WebCrypto.Tests.
[assembly: InternalsVisibleTo("Iris.WebCrypto.Tests")]
