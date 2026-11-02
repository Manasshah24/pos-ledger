// The tests open hundreds of connections to one local Postgres. Running test classes one at a time keeps connection
// setup from timing out; concurrency inside a test (the thing under test) is unaffected.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
