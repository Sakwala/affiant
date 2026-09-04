using Xunit;

// Each web-host test starts a real host with a background expiry sweep and its own SQLite files.
// Running them one at a time keeps the machine honest about what a slow assertion is waiting for,
// and the whole assembly finishes in a few seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
