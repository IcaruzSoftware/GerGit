using GerGit.Core;

namespace GerGit.Tests.Core;

public class RefNameTests
{
    [Theory]
    [InlineData("feature.", "Name cannot end with '.'")]
    [InlineData("a..b", "Name cannot contain '..'")]
    [InlineData("a~1", "Name cannot contain ASCII control characters '~', '^' or ':'")]
    [InlineData("a^", "Name cannot contain ASCII control characters '~', '^' or ':'")]
    [InlineData("a:b", "Name cannot contain ASCII control characters '~', '^' or ':'")]
    [InlineData("a?", "Name cannot contain '?', '*' or '['")]
    [InlineData("a*", "Name cannot contain '?', '*' or '['")]
    [InlineData("a[", "Name cannot contain '?', '*' or '['")]
    [InlineData("a@{b", "Name cannot contain '@{'")]
    [InlineData("a\\b", "Name cannot contain '\\'")]
    [InlineData("@", "Name cannot be the single '@' character")]
    [InlineData("/a", "Name cannot begin with '/'")]
    [InlineData("-m", "Name cannot begin with '-'")] // `git branch -m ...` would rename the active branch
    [InlineData("a/.b", "Name cannot contain '/.'")]
    [InlineData("a/b.lock", "Name cannot contain '/' and end with '.lock'")]
    [InlineData("a//b", "Name cannot contain multiple consecutive '/'")]
    // First failing rule wins: ends with "." (1) before ".." (2); "//" (12) after "/." (10); "-" (9) after "/" (8).
    [InlineData("a..", "Name cannot end with '.'")]
    [InlineData("a//.b", "Name cannot contain '/.'")]
    [InlineData("/-a", "Name cannot begin with '/'")]
    [InlineData("-a/.b", "Name cannot begin with '-'")]
    public void Invalid_names_give_the_first_failing_message(string name, string message) =>
        Assert.Equal((false, message), RefName.Validate(name));

    [Theory]
    [InlineData("main")]
    [InlineData("feature/login")]
    [InlineData("v1.0")]
    [InlineData("a.lock")]      // ".lock" alone is only checked together with "/"
    [InlineData("a-x")]
    [InlineData("a@b")]
    public void Valid_names(string name) => Assert.Equal((true, (string?)null), RefName.Validate(name));

    [Fact]
    public void Empty_name_is_invalid_without_message() => Assert.Equal((false, (string?)null), RefName.Validate(""));

    [Theory]
    [InlineData("my new branch", "my-new-branch")]
    [InlineData("a\r\nb\nc\rd", "a-b-c-d")]
    [InlineData("a..b//c@{d\\e/.f.lockg", "a-b-c-d-e-f-g")]
    [InlineData("x~y^z:w?v*u[t", "x-y-z-w-v-u-t")]
    [InlineData("@", "-")]
    [InlineData("a@b", "a@b")]
    [InlineData("feature/login", "feature/login")]
    public void Paste_is_cleaned(string pasted, string expected) => Assert.Equal(expected, RefName.CleanPaste(pasted, "-"));

    [Fact]
    public void Paste_uses_the_given_replacement() => Assert.Equal("a_b", RefName.CleanPaste("a b", "_"));
}
