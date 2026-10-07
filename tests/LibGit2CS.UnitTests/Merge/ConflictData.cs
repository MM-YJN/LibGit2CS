namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Expected conflict file contents from libgit2's
/// <c>tests/libgit2/merge/conflict_data.h</c>. Used by workdir merge tests
/// to verify the exact content of conflict-marker files on disk.
/// </summary>
internal static class ConflictData
{
    public const string AutomergeableMergedFile =
        "this file is changed in master\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is automergeable\n" +
        "this file is changed in branch\n";

    public const string AutomergeableMergedFileCrlf =
        "this file is changed in master\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is automergeable\r\n" +
        "this file is changed in branch\r\n";

    public const string ConflictingMergeFile =
        "<<<<<<< HEAD\n" +
        "this file is changed in master and branch\n" +
        "=======\n" +
        "this file is changed in branch and master\n" +
        ">>>>>>> 7cb63eed597130ba4abb87b3e544b85021905520\n";

    public const string ConflictingDiff3File =
        "<<<<<<< HEAD\n" +
        "this file is changed in master and branch\n" +
        "||||||| initial\n" +
        "this file is a conflict\n" +
        "=======\n" +
        "this file is changed in branch and master\n" +
        ">>>>>>> 7cb63eed597130ba4abb87b3e544b85021905520\n";

    public const string ConflictingZdiff3File =
        "<<<<<<< HEAD\n" +
        "this file is changed in master and branch\n" +
        "||||||| initial\n" +
        "this file is a conflict\n" +
        "=======\n" +
        "this file is changed in branch and master\n" +
        ">>>>>>> 7cb63eed597130ba4abb87b3e544b85021905520\n";

    public const string ConflictingUnionFile =
        "this file is changed in master and branch\n" +
        "this file is changed in branch and master\n";

    public const string ConflictingRecursiveF1ToF2 =
        "VEAL SOUP.\n" +
        "\n" +
        "<<<<<<< HEAD\n" +
        "PUT INTO A POT THREE QUARTS OF WATER, three onions cut small, ONE\n" +
        "=======\n" +
        "PUT INTO A POT THREE QUARTS OF WATER, three onions cut not too small, one\n" +
        ">>>>>>> branchF-2\n" +
        "spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "slices of lean ham; let it boil steadily two hours; skim it\n" +
        "occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "longer; take out the slices of ham, and skim off the grease if any\n" +
        "should rise, take a gill of good cream, mix with it two table-spoonsful\n" +
        "of flour very nicely, and the yelks of two eggs beaten well, strain this\n" +
        "mixture, and add some chopped parsley; pour some soup on by degrees,\n" +
        "stir it well, and pour it into the pot, continuing to stir until it has\n" +
        "boiled two or three minutes to take off the raw taste of the eggs. If\n" +
        "the cream be not perfectly sweet, and the eggs quite new, the thickening\n" +
        "will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        "in, first taking off their skins, by letting them stand a few minutes in\n" +
        "hot water, when they may be easily peeled. When made in this way you\n" +
        "must thicken it with the flour only. Any part of the veal may be used,\n" +
        "but the shin or knuckle is the nicest.\n" +
        "\n" +
        "<<<<<<< HEAD\n" +
        "This certainly is a mighty fine recipe.\n" +
        "=======\n" +
        "This is a mighty fine recipe!\n" +
        ">>>>>>> branchF-2\n";

    public const string ConflictingRecursiveH2ToH1WithDiff3 =
        "VEAL SOUP.\n" +
        "\n" +
        "<<<<<<< HEAD\n" +
        "Put Into A Pot Three Quarts of Water, Three Onions Cut Small, One\n" +
        "||||||| merged common ancestors\n" +
        "<<<<<<<<< Temporary merge branch 1\n" +
        "PUT INTO A POT three quarts of water, three onions cut small, one\n" +
        "||||||||| merged common ancestors\n" +
        "Put into a pot three quarts of water, three onions cut small, one\n" +
        "=========\n" +
        "Put into a pot three quarts of water, THREE ONIONS CUT SMALL, one\n" +
        ">>>>>>>>> Temporary merge branch 2\n" +
        "=======\n" +
        "put into a pot three quarts of water, three onions cut small, one\n" +
        ">>>>>>> branchH-1\n" +
        "spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "slices of lean ham; let it boil steadily two hours; skim it\n" +
        "occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "longer; take out the slices of ham, and skim off the grease if any\n" +
        "should rise, take a gill of good cream, mix with it two table-spoonsful\n" +
        "of flour very nicely, and the yelks of two eggs beaten well, strain this\n" +
        "mixture, and add some chopped parsley; pour some soup on by degrees,\n" +
        "stir it well, and pour it into the pot, continuing to stir until it has\n" +
        "boiled two or three minutes to take off the raw taste of the eggs. If\n" +
        "the cream be not perfectly sweet, and the eggs quite new, the thickening\n" +
        "will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        "in, first taking off their skins, by letting them stand a few minutes in\n" +
        "hot water, when they may be easily peeled. When made in this way you\n" +
        "must thicken it with the flour only. Any part of the veal may be used,\n" +
        "but the shin or knuckle is the nicest.\n";
}
