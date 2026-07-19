using Xunit;

// This suite forks child processes from several places — git (SemanticDiffTests), dotnet restore and
// MSBuildWorkspace (SampleSolutionFixture), and dotnet test (ladder level 5). Serializing collections
// keeps those forks from overlapping, which makes failures reproducible and keeps stray build servers
// from being attributed to the wrong test.
//
// NOTE: this is hygiene, not a bug fix — the fixture hang it was first added for turned out to be a
// persistent MSBuild node inheriting redirected pipes (see RunDotnet), not test parallelism. The fast
// tests total well under a second and the MSBuild load dominates regardless, so serializing is
// effectively free.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
