using StardewLauncher.Core.App;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>收藏的一位队友（对外只读快照）。</summary>
public sealed record Friend(string Nickname, string LastIp, string LastRoom, int Times, DateTime LastSeenUtc);

/// <summary>
/// 队友收藏夹。数据落在设置文件里，纯本地、不上传——昵称列表本身也算社交关系，没必要传出去。
///
/// 以昵称为主键：EasyTier 的虚拟 IP 每次开房都会变（实测同一台机器两次是 10.126.126.1 / 10.126.126.2），
/// 拿 IP 认人会把老朋友认成新人。IP 与房间名只留作「上次见到时」的参考。
/// </summary>
public static class FriendStore
{
    /// <summary>按最近一起玩的时间倒序返回。</summary>
    public static IReadOnlyList<Friend> List()
    {
        var records = Current();

        return [.. records
            .Where(r => !string.IsNullOrWhiteSpace(r.Nickname))
            .OrderByDescending(r => r.LastSeenUtc)
            .Select(r => new Friend(r.Nickname, r.LastIp, r.LastRoom, r.Times, r.LastSeenUtc))];
    }

    /// <summary>记一笔「刚和这个人一起玩过」。同名视为同一人，累加次数并刷新最近时间。</summary>
    public static void Remember(string nickname, string ip = "", string room = "")
    {
        var name = (nickname ?? string.Empty).Trim();
        if (name.Length == 0) return;

        var records = Current();
        var now = DateTime.UtcNow;

        var existing = records.FirstOrDefault(r => string.Equals(r.Nickname, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Nickname = name;
            if (ip.Length > 0) existing.LastIp = ip;
            if (room.Length > 0) existing.LastRoom = room;
            existing.Times++;
            existing.LastSeenUtc = now;
        }
        else
        {
            records.Add(new FriendRecord
            {
                Nickname = name,
                LastIp = ip,
                LastRoom = room,
                Times = 1,
                LastSeenUtc = now
            });
        }

        SettingsStore.Save();
    }

    public static void Remove(string nickname)
    {
        var records = Current();

        var removed = records.RemoveAll(
            r => string.Equals(r.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

        if (removed > 0) SettingsStore.Save();
    }

    public static void Clear()
    {
        if (Current().Count == 0) return;

        Current().Clear();
        SettingsStore.Save();
    }

    /// <summary>设置文件是人可编辑的，手改出 null 不该让界面崩掉。</summary>
    private static List<FriendRecord> Current()
        => SettingsStore.Current.MultiplayerFriends ??= [];
}
