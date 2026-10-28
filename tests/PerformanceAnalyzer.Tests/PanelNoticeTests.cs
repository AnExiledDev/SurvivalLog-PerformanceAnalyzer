using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>Notices, such as the trace key's, have to show with the panel closed.</summary>
public class PanelNoticeTests
{
    private static string Markup()
    {
        var js = new Engine();

        js.Execute(PanelUiSizeTests.FakePage);
        js.Execute("var shadow; var attach = Node.prototype.attachShadow; Node.prototype.attachShadow = function () { return shadow = attach.call(this); };");
        js.Execute(PageTests.Web("format.js"));
        js.Execute(PageTests.Web("panel.js"));

        return js.Evaluate("shadow.innerHTML").AsString();
    }

    [Fact]
    public void The_notice_is_not_inside_the_panel_that_is_hidden_while_closed()
    {
        var html = Markup();
        var panel = html.IndexOf("<div id=\"panel\"", System.StringComparison.Ordinal);
        var notice = html.IndexOf("<div id=\"toast\"", System.StringComparison.Ordinal);

        Assert.True(panel >= 0 && notice >= 0);

        // Walk the divs from the panel's opening tag to the one that closes it.
        var depth = 0;
        var end = -1;

        foreach (Match tag in Regex.Matches(html.Substring(panel), "<div\\b|</div>"))
        {
            depth += tag.Value == "</div>" ? -1 : 1;

            if (depth == 0)
            {
                end = panel + tag.Index;
                break;
            }
        }

        Assert.True(end > panel, "the panel's div is never closed");
        Assert.True(notice > end || notice < panel, "the notice sits inside the panel");
    }
}
