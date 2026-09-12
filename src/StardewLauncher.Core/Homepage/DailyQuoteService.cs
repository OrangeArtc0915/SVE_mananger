namespace StardewLauncher.Core.Homepage;

/// <summary>
/// 每日一言。目前用内置语料随机取，同一句不会连续出现两次。
/// 将来要接远端语料时，只需替换 Pick 的实现。
/// </summary>
public static class DailyQuoteService
{
    private static readonly string[] Quotes =
    [
        "农场不会自己长好，但每天浇一次水就够了。",
        "别急着把整座山谷摸透，先把今天过好。",
        "下雨天不用浇水，正好去矿洞看看。",
        "送礼要看人，也要看季节。",
        "背包满了就先回家，明天再去也一样。",
        "第一年种地靠力气，第二年靠计划。",
        "把种子埋在土里，剩下的交给时间。",
        "邻居的名字记熟了，村子才算真的住下。",
        "鱼上钩的那一下，值得等一整个下午。",
        "矿洞里的电梯，是给有耐心的人准备的。",
        "装修不是为了好看，是为了每天回来心情好。",
        "慢一点没关系，这里的日子很长。"
    ];

    private static int _lastIndex = -1;
    private static readonly Random Random = new();

    public static string Pick()
    {
        if (Quotes.Length == 1) return Quotes[0];

        var index = Random.Next(Quotes.Length);
        if (index == _lastIndex) index = (index + 1) % Quotes.Length;

        _lastIndex = index;
        return Quotes[index];
    }
}
