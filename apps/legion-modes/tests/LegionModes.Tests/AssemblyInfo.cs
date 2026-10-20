using Xunit;

// Tr (the interface language) is process-wide state that several tests set; the suite takes seconds, so it runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
