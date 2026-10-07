using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Patch text constants for apply tests. Ported verbatim from
/// <c>tests/libgit2/apply/apply_helpers.h</c> — the <c>DIFF_*</c> macros.
/// Each constant is a unified diff buffer suitable for
/// <see cref="GitDiff.FromBuffer"/>.
/// </summary>
internal static class ApplyTestData
{
    /// <summary>Modifies asparagus.txt and veal.txt (1 line each + trailing hunk).</summary>
    public const string ModifyTwoFiles =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "index f516580..ffb36e5 100644\n" +
        "--- a/asparagus.txt\n" +
        "+++ b/asparagus.txt\n" +
        "@@ -1 +1 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "+ASPARAGUS SOUP.\n" +
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01..a7b0665 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1 +1 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP.\n" +
        "@@ -7 +7 @@ occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "-longer. take out the slices of ham, and skim off the grease if any\n" +
        "+longer; take out the slices of ham, and skim off the grease if any\n";

    /// <summary>Binary equivalent of <see cref="ModifyTwoFiles"/>.</summary>
    public const string ModifyTwoFilesBinary =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "index f51658077d85f2264fa179b4d0848268cb3475c3..ffb36e513f5fdf8a6ba850a20142676a2ac4807d 100644\n" +
        "GIT binary patch\n" +
        "delta 24\n" +
        "fcmX@ja+-zTF*v|6$k9DCSRvRyG(c}7zYP-rT_OhP\n" +
        "\n" +
        "delta 24\n" +
        "fcmX@ja+-zTF*v|6$k9DCSRvRyG(d49zYP-rT;T@W\n" +
        "\n" +
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01087f48213bd157222d54edfefd77c9bba..a7b066537e6be7109abfe4ff97b675d4e077da20 100644\n" +
        "GIT binary patch\n" +
        "delta 26\n" +
        "hcmX@kah!uI%+=9HA=p1OKyM?L03)OIW@$zpW&mXg25bNT\n" +
        "\n" +
        "delta 26\n" +
        "hcmX@kah!uI%+=9HA=p1OKyf3N03)N`W@$zpW&mU#22ub3\n" +
        "\n";

    /// <summary>Deletes gravy.txt.</summary>
    public const string DeleteFile =
        "diff --git a/gravy.txt b/gravy.txt\n" +
        "deleted file mode 100644\n" +
        "index c4e6cca..0000000\n" +
        "--- a/gravy.txt\n" +
        "+++ /dev/null\n" +
        "@@ -1,8 +0,0 @@\n" +
        "-GRAVY SOUP.\n" +
        "-\n" +
        "-Get eight pounds of coarse lean beef--wash it clean and lay it in your\n" +
        "-pot, put in the same ingredients as for the shin soup, with the same\n" +
        "-quantity of water, and follow the process directed for that. Strain the\n" +
        "-soup through a sieve, and serve it up clear, with nothing more than\n" +
        "-toasted bread in it; two table-spoonsful of mushroom catsup will add a\n" +
        "-fine flavour to the soup.\n";

    /// <summary>Adds newfile.txt (2 lines).</summary>
    public const string AddFile =
        "diff --git a/newfile.txt b/newfile.txt\n" +
        "new file mode 100644\n" +
        "index 0000000..6370543\n" +
        "--- /dev/null\n" +
        "+++ b/newfile.txt\n" +
        "@@ -0,0 +1,2 @@\n" +
        "+This is a new file!\n" +
        "+Added by a patch.\n";

    /// <summary>Changes beef.txt mode from 100644 to 100755.</summary>
    public const string ExecutableFile =
        "diff --git a/beef.txt b/beef.txt\n" +
        "old mode 100644\n" +
        "new mode 100755\n";

    /// <summary>Multiple hunks on veal.txt (5 hunks: modify, delete, case-change, insert, modify).</summary>
    public const string ManyChangesOne =
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01..c9d7d5d 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1,2 +1,2 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP\n" +
        " \n" +
        "@@ -4,3 +4,2 @@\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "-slices of lean ham; let it boil steadily two hours; skim it\n" +
        " occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "@@ -8,3 +7,3 @@\n" +
        " should rise, take a gill of good cream, mix with it two table-spoonsful\n" +
        "-of flour very nicely, and the yelks of two eggs beaten well, strain this\n" +
        "+OF FLOUR very nicely, and the yelks of two eggs beaten well, strain this\n" +
        " mixture, and add some chopped parsley; pour some soup on by degrees,\n" +
        "@@ -12,2 +11,3 @@\n" +
        " boiled two or three minutes to take off the raw taste of the eggs. If\n" +
        "+Inserted line.\n" +
        " the cream be not perfectly sweet, and the eggs quite new, the thickening\n" +
        "@@ -15,3 +15,3 @@\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        "-hot water, when they may be easily peeled. When made in this way you\n" +
        "+Changed line.\n" +
        " must thicken it with the flour only. Any part of the veal may be used,\n";

    /// <summary>Multiple hunks on veal.txt (5 hunks with different changes).</summary>
    public const string ManyChangesTwo =
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01..6b943d6 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1,2 +1,2 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP!!!\n" +
        " \n" +
        "@@ -4,3 +4,2 @@\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "-slices of lean ham; let it boil steadily two hours; skim it\n" +
        " occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "@@ -8,3 +7,3 @@\n" +
        " should rise, take a gill of good cream, mix with it two table-spoonsful\n" +
        "-of flour very nicely, and the yelks of two eggs beaten well, strain this\n" +
        "+of flour very nicely, AND the yelks of two eggs beaten well, strain this\n" +
        " mixture, and add some chopped parsley; pour some soup on by degrees,\n" +
        "@@ -12,2 +11,3 @@\n" +
        " boiled two or three minutes to take off the raw taste of the eggs. If\n" +
        "+New line.\n" +
        " the cream be not perfectly sweet, and the eggs quite new, the thickening\n" +
        "@@ -15,4 +15,5 @@\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        "-hot water, when they may be easily peeled. When made in this way you\n" +
        "-must thicken it with the flour only. Any part of the veal may be used,\n" +
        "-but the shin or knuckle is the nicest.\n" +
        "+HOT water, when they may be easily peeled. When made in this way you\n" +
        "+must THICKEN it with the flour only. Any part of the veal may be used,\n" +
        "+but the shin OR knuckle is the nicest.\n" +
        "+Another new line.\n";

    /// <summary>Pure rename beef.txt → notbeef.txt (100% similarity).</summary>
    public const string RenameFile =
        "diff --git a/beef.txt b/notbeef.txt\n" +
        "similarity index 100%\n" +
        "rename from beef.txt\n" +
        "rename to notbeef.txt\n";

    /// <summary>Rename + modify beef.txt → notbeef.txt (97% similarity).</summary>
    public const string RenameAndModifyFile =
        "diff --git a/beef.txt b/notbeef.txt\n" +
        "similarity index 97%\n" +
        "rename from beef.txt\n" +
        "rename to notbeef.txt\n" +
        "index 68f6182..6fa1014 100644\n" +
        "--- a/beef.txt\n" +
        "+++ b/notbeef.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-BEEF SOUP.\n" +
        "+THIS IS NOT BEEF SOUP, IT HAS A NEW NAME.\n" +
        "\n" +
        " Take the hind shin of beef, cut off all the flesh off the leg-bone,\n" +
        " which must be taken away entirely, or the soup will be greasy. Wash the\n";

    /// <summary>Delete asparagus.txt, modify beef.txt to asparagus content, add notbeef.txt with beef content.</summary>
    public const string RenameAToBToC =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "deleted file mode 100644\n" +
        "index f516580..0000000\n" +
        "--- a/asparagus.txt\n" +
        "+++ /dev/null\n" +
        "@@ -1,10 +0,0 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "-\n" +
        "-Take four large bunches of asparagus, scrape it nicely, cut off one inch\n" +
        "-of the tops, and lay them in water, chop the stalks and put them on the\n" +
        "-fire with a piece of bacon, a large onion cut up, and pepper and salt;\n" +
        "-add two quarts of water, boil them till the stalks are quite soft, then\n" +
        "-pulp them through a sieve, and strain the water to it, which must be put\n" +
        "-back in the pot; put into it a chicken cut up, with the tops of\n" +
        "-asparagus which had been laid by, boil it until these last articles are\n" +
        "-sufficiently done, thicken with flour, butter and milk, and serve it up.\n" +
        "diff --git a/beef.txt b/beef.txt\n" +
        "index 68f6182..f516580 100644\n" +
        "--- a/beef.txt\n" +
        "+++ b/beef.txt\n" +
        "@@ -1,22 +1,10 @@\n" +
        "-BEEF SOUP.\n" +
        "+ASPARAGUS SOUP!\n" +
        "\n" +
        "-Take the hind shin of beef, cut off all the flesh off the leg-bone,\n" +
        "-which must be taken away entirely, or the soup will be greasy. Wash the\n" +
        "-meat clean and lay it in a pot, sprinkle over it one small\n" +
        "-table-spoonful of pounded black pepper, and two of salt; three onions\n" +
        "-the size of a hen's egg, cut small, six small carrots scraped and cut\n" +
        "-up, two small turnips pared and cut into dice; pour on three quarts of\n" +
        "-water, cover the pot close, and keep it gently and steadily boiling five\n" +
        "-hours, which will leave about three pints of clear soup; do not let the\n" +
        "-pot boil over, but take off the scum carefully, as it rises. When it has\n" +
        "-boiled four hours, put in a small bundle of thyme and parsley, and a\n" +
        "-pint of celery cut small, or a tea-spoonful of celery seed pounded.\n" +
        "-These latter ingredients would lose their delicate flavour if boiled too\n" +
        "-much. Just before you take it up, brown it in the following manner: put\n" +
        "-a small table-spoonful of nice brown sugar into an iron skillet, set it\n" +
        "-on the fire and stir it till it melts and looks very dark, pour into it\n" +
        "-a ladle full of the soup, a little at a time; stirring it all the while.\n" +
        "-Strain this browning and mix it well with the soup; take out the bundle\n" +
        "-of thyme and parsley, put the nicest pieces of meat in your tureen, and\n" +
        "-pour on the soup and vegetables; put in some toasted bread cut in dice,\n" +
        "-and serve it up.\n" +
        "+Take four large bunches of asparagus, scrape it nicely, cut off one inch\n" +
        "+of the tops, and lay them in water, chop the stalks and put them on the\n" +
        "+fire with a piece of bacon, a large onion cut up, and pepper and salt;\n" +
        "+add two quarts of water, boil them till the stalks are quite soft, then\n" +
        "+pulp them through a sieve, and strain the water to it, which must be put\n" +
        "+back in the pot; put into it a chicken cut up, with the tops of\n" +
        "+asparagus which had been laid by, boil it until these last articles are\n" +
        "+sufficiently done, thicken with flour, butter and milk, and serve it up.\n" +
        "diff --git a/notbeef.txt b/notbeef.txt\n" +
        "new file mode 100644\n" +
        "index 0000000..68f6182\n" +
        "--- /dev/null\n" +
        "+++ b/notbeef.txt\n" +
        "@@ -0,0 +1,22 @@\n" +
        "+BEEF SOUP.\n" +
        "+\n" +
        "+Take the hind shin of beef, cut off all the flesh off the leg-bone,\n" +
        "+which must be taken away entirely, or the soup will be greasy. Wash the\n" +
        "+meat clean and lay it in a pot, sprinkle over it one small\n" +
        "+table-spoonful of pounded black pepper, and two of salt; three onions\n" +
        "+the size of a hen's egg, cut small, six small carrots scraped and cut\n" +
        "+up, two small turnips pared and cut into dice; pour on three quarts of\n" +
        "+water, cover the pot close, and keep it gently and steadily boiling five\n" +
        "+hours, which will leave about three pints of clear soup; do not let the\n" +
        "+pot boil over, but take off the scum carefully, as it rises. When it has\n" +
        "+boiled four hours, put in a small bundle of thyme and parsley, and a\n" +
        "+pint of celery cut small, or a tea-spoonful of celery seed pounded.\n" +
        "+These latter ingredients would lose their delicate flavour if boiled too\n" +
        "+much. Just before you take it up, brown it in the following manner: put\n" +
        "+a small table-spoonful of nice brown sugar into an iron skillet, set it\n" +
        "+on the fire and stir it till it melts and looks very dark, pour into it\n" +
        "+a ladle full of the soup, a little at a time; stirring it all the while.\n" +
        "+Strain this browning and mix it well with the soup; take out the bundle\n" +
        "+of thyme and parsley, put the nicest pieces of meat in your tureen, and\n" +
        "+pour on the soup and vegetables; put in some toasted bread cut in dice,\n" +
        "+and serve it up.\n";

    /// <summary>Exact renames: asparagus.txt → beef.txt → notbeef.txt.</summary>
    public const string RenameAToBToCExact =
        "diff --git a/asparagus.txt b/beef.txt\n" +
        "similarity index 100%\n" +
        "rename from asparagus.txt\n" +
        "rename to beef.txt\n" +
        "diff --git a/beef.txt b/notbeef.txt\n" +
        "similarity index 100%\n" +
        "rename from beef.txt\n" +
        "rename to notbeef.txt\n";

    /// <summary>Circular rename: asparagus.txt → beef.txt, beef.txt → asparagus.txt.</summary>
    public const string RenameCircular =
        "diff --git a/asparagus.txt b/beef.txt\n" +
        "similarity index 100%\n" +
        "rename from asparagus.txt\n" +
        "rename to beef.txt\n" +
        "diff --git a/beef.txt b/notbeef.txt\n" +
        "similarity index 100%\n" +
        "rename from beef.txt\n" +
        "rename to asparagus.txt\n";

    /// <summary>Two files renamed to same target (asparagus.txt, beef.txt → 2.txt).</summary>
    public const string Rename2To1 =
        "diff --git a/asparagus.txt b/2.txt\n" +
        "similarity index 100%\n" +
        "rename from asparagus.txt\n" +
        "rename to 2.txt\n" +
        "diff --git a/beef.txt b/2.txt\n" +
        "similarity index 100%\n" +
        "rename from beef.txt\n" +
        "rename to 2.txt\n";

    /// <summary>One file renamed to two targets (asparagus.txt → 1.txt and 2.txt).</summary>
    public const string Rename1To2 =
        "diff --git a/asparagus.txt b/2.txt\n" +
        "similarity index 100%\n" +
        "rename from asparagus.txt\n" +
        "rename to 1.txt\n" +
        "diff --git a/asparagus.txt b/2.txt\n" +
        "similarity index 100%\n" +
        "rename from asparagus.txt\n" +
        "rename to 2.txt\n";

    /// <summary>Two independent modifications to beef.txt (different regions).</summary>
    public const string TwoDeltasOneFile =
        "diff --git a/beef.txt b/beef.txt\n" +
        "index 68f6182..235069d 100644\n" +
        "--- a/beef.txt\n" +
        "+++ b/beef.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-BEEF SOUP.\n" +
        "+BEEF SOUP!\n" +
        "\n" +
        " Take the hind shin of beef, cut off all the flesh off the leg-bone,\n" +
        " which must be taken away entirely, or the soup will be greasy. Wash the\n" +
        "diff --git a/beef.txt b/beef.txt\n" +
        "index 68f6182..e059eb5 100644\n" +
        "--- a/beef.txt\n" +
        "+++ b/beef.txt\n" +
        "@@ -19,4 +19,4 @@ a ladle full of the soup, a little at a time; stirring it all the while.\n" +
        " Strain this browning and mix it well with the soup; take out the bundle\n" +
        " of thyme and parsley, put the nicest pieces of meat in your tureen, and\n" +
        " pour on the soup and vegetables; put in some toasted bread cut in dice,\n" +
        "-and serve it up.\n" +
        "+and serve it up!\n";

    /// <summary>Create then modify newfile.txt.</summary>
    public const string TwoDeltasOneNewFile =
        "diff --git a/newfile.txt b/newfile.txt\n" +
        "new file mode 100644\n" +
        "index 0000000..6434b13\n" +
        "--- /dev/null\n" +
        "+++ b/newfile.txt\n" +
        "@@ -0,0 +1 @@\n" +
        "+This is a new file.\n" +
        "diff --git a/newfile.txt b/newfile.txt\n" +
        "index 6434b13..08d4c44 100644\n" +
        "--- a/newfile.txt\n" +
        "+++ b/newfile.txt\n" +
        "@@ -1 +1,3 @@\n" +
        " This is a new file.\n" +
        "+\n" +
        "+This is another change to a new file.\n";

    /// <summary>Rename veal.txt → asdf.txt with modification, then further modify asdf.txt.</summary>
    public const string RenameAndModifyDeltas =
        "diff --git a/veal.txt b/asdf.txt\n" +
        "similarity index 96%\n" +
        "rename from veal.txt\n" +
        "rename to asdf.txt\n" +
        "index 94d2c01..292cb60 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/asdf.txt\n" +
        "@@ -15,4 +15,4 @@ will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        " hot water, when they may be easily peeled. When made in this way you\n" +
        " must thicken it with the flour only. Any part of the veal may be used,\n" +
        "-but the shin or knuckle is the nicest.\n" +
        "+but the shin or knuckle is the nicest!\n" +
        "diff --git a/asdf.txt b/asdf.txt\n" +
        "index 292cb60..61c686b 100644\n" +
        "--- a/asdf.txt\n" +
        "+++ b/asdf.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP\n" +
        "\n" +
        " Put into a pot three quarts of water, three onions cut small, one\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n";

    /// <summary>Modify veal.txt, then rename veal.txt → other.txt.</summary>
    public const string RenameAfterModify =
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 292cb60..61c686b 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP\n" +
        "\n" +
        " Put into a pot three quarts of water, three onions cut small, one\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "diff --git a/veal.txt b/other.txt\n" +
        "similarity index 96%\n" +
        "rename from veal.txt\n" +
        "rename to other.txt\n" +
        "index 94d2c01..292cb60 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/other.txt\n" +
        "@@ -15,4 +15,4 @@ will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        " hot water, when they may be easily peeled. When made in this way you\n" +
        " must thicken it with the flour only. Any part of the veal may be used,\n" +
        "-but the shin or knuckle is the nicest.\n" +
        "+but the shin or knuckle is the nicest!\n";

    /// <summary>Modify beef.txt, then rename veal.txt → beef.txt (target path conflict — should fail).</summary>
    public const string RenameAfterModifyTargetPath =
        "diff --git a/beef.txt b/beef.txt\n" +
        "index 292cb60..61c686b 100644\n" +
        "--- a/beef.txt\n" +
        "+++ b/beef.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP\n" +
        "\n" +
        " Put into a pot three quarts of water, three onions cut small, one\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n" +
        "diff --git a/veal.txt b/beef.txt\n" +
        "similarity index 96%\n" +
        "rename from veal.txt\n" +
        "rename to beef.txt\n" +
        "index 94d2c01..292cb60 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/beef.txt\n" +
        "@@ -15,4 +15,4 @@ will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        " hot water, when they may be easily peeled. When made in this way you\n" +
        " must thicken it with the flour only. Any part of the veal may be used,\n" +
        "-but the shin or knuckle is the nicest.\n" +
        "+but the shin or knuckle is the nicest!\n";

    /// <summary>Rename+modify veal.txt → asdf.txt, then independently modify veal.txt (source path conflict — should fail).</summary>
    public const string RenameAndModifySourcePath =
        "diff --git a/veal.txt b/asdf.txt\n" +
        "similarity index 96%\n" +
        "rename from veal.txt\n" +
        "rename to asdf.txt\n" +
        "index 94d2c01..292cb60 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/asdf.txt\n" +
        "@@ -15,4 +15,4 @@ will curdle in the soup. For a change you may put a dozen ripe tomatos\n" +
        " in, first taking off their skins, by letting them stand a few minutes in\n" +
        " hot water, when they may be easily peeled. When made in this way you\n" +
        " must thicken it with the flour only. Any part of the veal may be used,\n" +
        "-but the shin or knuckle is the nicest.\n" +
        "+but the shin or knuckle is the nicest!\n" +
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 292cb60..61c686b 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP\n" +
        "\n" +
        " Put into a pot three quarts of water, three onions cut small, one\n" +
        " spoonful of black pepper pounded, and two of salt, with two or three\n";

    /// <summary>Delete asparagus.txt then re-add it as a new file.</summary>
    public const string DeleteAndReaddFile =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "deleted file mode 100644\n" +
        "index f516580..0000000\n" +
        "--- a/asparagus.txt\n" +
        "+++ /dev/null\n" +
        "@@ -1,10 +0,0 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "-\n" +
        "-Take four large bunches of asparagus, scrape it nicely, cut off one inch\n" +
        "-of the tops, and lay them in water, chop the stalks and put them on the\n" +
        "-fire with a piece of bacon, a large onion cut up, and pepper and salt;\n" +
        "-add two quarts of water, boil them till the stalks are quite soft, then\n" +
        "-pulp them through a sieve, and strain the water to it, which must be put\n" +
        "-back in the pot; put into it a chicken cut up, with the tops of\n" +
        "-asparagus which had been laid by, boil it until these last articles are\n" +
        "-sufficiently done, thicken with flour, butter and milk, and serve it up.\n" +
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "new file mode 100644\n" +
        "index 0000000..2dc7f8b\n" +
        "--- /dev/null\n" +
        "+++ b/asparagus.txt\n" +
        "@@ -0,0 +1 @@\n" +
        "+New file.\n";

    /// <summary>Delete asparagus.txt twice (duplicate delete — should fail).</summary>
    public const string RemoveFileTwice =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "deleted file mode 100644\n" +
        "index f516580..0000000\n" +
        "--- a/asparagus.txt\n" +
        "+++ /dev/null\n" +
        "@@ -1,10 +0,0 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "-\n" +
        "-Take four large bunches of asparagus, scrape it nicely, cut off one inch\n" +
        "-of the tops, and lay them in water, chop the stalks and put them on the\n" +
        "-fire with a piece of bacon, a large onion cut up, and pepper and salt;\n" +
        "-add two quarts of water, boil them till the stalks are quite soft, then\n" +
        "-pulp them through a sieve, and strain the water to it, which must be put\n" +
        "-back in the pot; put into it a chicken cut up, with the tops of\n" +
        "-asparagus which had been laid by, boil it until these last articles are\n" +
        "-sufficiently done, thicken with flour, butter and milk, and serve it up.\n" +
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "deleted file mode 100644\n" +
        "index f516580..0000000\n" +
        "--- a/asparagus.txt\n" +
        "+++ /dev/null\n" +
        "@@ -1,10 +0,0 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "-\n" +
        "-Take four large bunches of asparagus, scrape it nicely, cut off one inch\n" +
        "-of the tops, and lay them in water, chop the stalks and put them on the\n" +
        "-fire with a piece of bacon, a large onion cut up, and pepper and salt;\n" +
        "-add two quarts of water, boil them till the stalks are quite soft, then\n" +
        "-pulp them through a sieve, and strain the water to it, which must be put\n" +
        "-back in the pot; put into it a chicken cut up, with the tops of\n" +
        "-asparagus which had been laid by, boil it until these last articles are\n" +
        "-sufficiently done, thicken with flour, butter and milk, and serve it up.\n";

    /// <summary>Attempt to add file under .git/ (invalid filename — should fail).</summary>
    public const string AddInvalidFilename =
        "diff --git a/.git/hello_world.txt b/.git/hello_world.txt\n" +
        "new file mode 100644\n" +
        "index 0000000..f75ba05\n" +
        "--- /dev/null\n" +
        "+++ b/.git/hello_world.txt\n" +
        "@@ -0,0 +1 @@\n" +
        "+Hello, world.\n";
}
