using System.Runtime.CompilerServices;

// Allow the test project to access internal factory methods (BulkRun.Create, BulkRunItem.Create)
// used in NoOpStrategy stubs. The production run strategies (WP-2/3/4) live in the same module
// and access these as internal; test stubs mirror that role.
[assembly: InternalsVisibleTo("LeaseBook.Tests.Operations")]
