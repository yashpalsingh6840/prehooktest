using Xunit;

// Serializes every test CLASS in this assembly against every other one (tests WITHIN a class
// were already sequential by xUnit's own default -- this is about DIFFERENT classes, which
// xUnit otherwise runs in parallel with each other).
//
// Required because several test classes here (ApplyFillsCommandTests, RobustnessTests,
// ExitCodeContractTests) call a command's Run(...) in-process and temporarily redirect the
// process-wide static Console.Out/Console.Error to capture output -- RunLog.Start (used by
// ApplyFillsCommand/GenerateCommand themselves) does the same. Two of these racing on different
// threads corrupts each other's redirection, surfacing as an unrelated test failing with
// "System.ObjectDisposedException: Cannot write to a closed TextWriter" -- confirmed exactly
// this way once a second Console-redirecting test class was added (Phase 1.5 of the
// independent-review fix plan). None of these tests spawn real subprocesses, so serializing them
// costs nothing worth mentioning.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
