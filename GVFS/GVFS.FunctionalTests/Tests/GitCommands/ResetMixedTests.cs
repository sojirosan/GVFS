﻿using GVFS.FunctionalTests.Should;
using NUnit.Framework;

namespace GVFS.FunctionalTests.Tests.GitCommands
{
    [TestFixture]
    [Category(Categories.GitCommands)]
    public class ResetMixedTests : GitRepoTests
    {
        public ResetMixedTests() : base(enlistmentPerTest: true)
        {
        }

        [TestCase]
        public void ResetMixed()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");
            this.FilesShouldMatchCheckoutOfTargetBranch();
        }

        [TestCase]
        public void ResetMixedAndCheckoutNewBranch()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");

            // Use RunGitCommand rather than ValidateGitCommand as G4W optimizations for "checkout -b" mean that the
            // command will not report modified and deleted files
            this.RunGitCommand("checkout -b tests/functional/ResetMixedAndCheckoutNewBranch");
            this.FilesShouldMatchCheckoutOfTargetBranch();
            this.ValidateGitCommand("status");            
        }

        [TestCase]
        [Ignore("939998 - git checkout --orphan after git reset --mixed does not report deleted files on GVFS ")]
        public void ResetMixedAndCheckoutOrphanBranch()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");
            this.ValidateGitCommand("checkout --orphan tests/functional/ResetMixedAndCheckoutOrphanBranch");
            this.FilesShouldMatchCheckoutOfTargetBranch();
        }

        [TestCase]
        public void ResetMixedAndRemount()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");
            this.FilesShouldMatchCheckoutOfTargetBranch();

            this.Enlistment.UnmountGVFS();
            this.Enlistment.MountGVFS();
            this.ValidateGitCommand("status");
            this.FilesShouldMatchCheckoutOfTargetBranch();
        }

        [TestCase]
        public void ResetMixedThenCheckoutWithConflicts()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");

            // Because git while using the sparse-checkout feature
            // will check for index merge conflicts and error out before it checks
            // for untracked files that will be overwritten we just run the command
            this.RunGitCommand("checkout " + GitRepoTests.ConflictSourceBranch, ignoreErrors: true);
            this.FilesShouldMatchCheckoutOfTargetBranch();
        }

        [TestCase]
        public void ResetMixedOnlyAddedThenCheckoutWithConflicts()
        {
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictTargetBranch);
            this.ValidateGitCommand("reset --mixed HEAD~1");

            // This will reset all the files except the files that were added 
            // and are untracked to make sure we error out with those using sparse-checkout
            this.ValidateGitCommand("checkout -f");
            this.ValidateGitCommand("checkout " + GitRepoTests.ConflictSourceBranch);
            this.FilesShouldMatchCheckoutOfTargetBranch();
        }

        [TestCase]
        public void ResetMixedAndCheckoutFile()
        {
            this.ControlGitRepo.Fetch("FunctionalTests/20170602");

            // We start with a branch that deleted two files that were present in its parent commit
            this.ValidateGitCommand("checkout FunctionalTests/20170602");

            // Then reset --mixed to the parent commit, and validate that the deleted files did not come back into the projection
            this.ValidateGitCommand("reset --mixed HEAD~1");
            this.Enlistment.RepoRoot.ShouldBeADirectory(this.FileSystem)
                .WithDeepStructure(this.FileSystem, this.ControlGitRepo.RootPath);

            // And checkout a file (without changing branches) and ensure that that doesn't update the projection either
            this.ValidateGitCommand("checkout HEAD~2 .gitattributes");
            this.Enlistment.RepoRoot.ShouldBeADirectory(this.FileSystem)
                .WithDeepStructure(this.FileSystem, this.ControlGitRepo.RootPath);

            // And now if we checkout the original commit, the deleted files should stay deleted
            this.ValidateGitCommand("checkout FunctionalTests/20170602");
            this.Enlistment.RepoRoot.ShouldBeADirectory(this.FileSystem)
                .WithDeepStructure(this.FileSystem, this.ControlGitRepo.RootPath);
        }

        protected override void CreateEnlistment()
        {
            base.CreateEnlistment();
            this.ControlGitRepo.Fetch(GitRepoTests.ConflictTargetBranch);
            this.ControlGitRepo.Fetch(GitRepoTests.ConflictSourceBranch);
        }
    }
}
