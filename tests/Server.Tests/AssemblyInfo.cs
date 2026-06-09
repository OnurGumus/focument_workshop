// The integration tier stands up one Akka node for the whole run; run tests
// sequentially so a single actor system and database are never shared across
// concurrently-executing tests.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
