using System.Runtime.CompilerServices;

// Allow the test projects to access internal factory methods (BulkRun.Create, BulkRunItem.Create)
// used in strategy stubs. The production run strategies (WP-2/3/4) live in the same module and
// access these as internal; test stubs mirror that role. LeaseBook.Tests.Integration needs the same
// access for the capability-freeze suite, whose strategy double must emit real BulkRunItem rows to
// assert against what the run actually persisted; Accounting and Capabilities already grant the same
// assembly access for the same reason.
[assembly: InternalsVisibleTo("LeaseBook.Tests.Operations")]
[assembly: InternalsVisibleTo("LeaseBook.Tests.Integration")]
