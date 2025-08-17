using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

public class ChatHub : Hub
{
    // 存储在线用户列表
    private static readonly HashSet<User> _users = new();
    
    // 消息垃圾防护 - 用户消息计数器
    private static readonly ConcurrentDictionary<string, UserMessageTracker> _messageTrackers = new();
    
    // 提供对用户列表的只读访问
    public IEnumerable<User> Users => _users;
    
    // 静态方法检查用户是否在线
    public static bool IsUserOnline(string name)
    {
        return _users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public override async Task OnConnectedAsync()
    {
        var name = Context.GetHttpContext()?.Request.Query["name"].ToString();
        if (string.IsNullOrEmpty(name))
        {
            Context.Abort();
            return;
        }

        // 忽略临时连接
        if (name == "__temp_checker__")
        {
            // 只发送当前用户列表，不添加到用户列表中
            await Clients.Caller.SendAsync(
                "UpdateStatus",
                _users.Count,
                "获取用户列表",
                _users.Select(u => u.Name)
            );
            return;
        }

        // 检查用户名是否已存在
        if (_users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Context.Abort();
            return;
        }

        var user = new User
        {
            ConnectionId = Context.ConnectionId,
            Name = name
        };

        _users.Add(user);
        
        // 初始化用户消息跟踪器
        _messageTrackers[Context.ConnectionId] = new UserMessageTracker();

        await Clients.All.SendAsync(
            "UpdateStatus",
            _users.Count,
            $"{name} joined the chat",
            _users.Select(u => u.Name)
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var user = _users.FirstOrDefault(u => u.ConnectionId == Context.ConnectionId);
        if (user != null)
        {
            _users.Remove(user);
            
            // 移除用户消息跟踪器
            _messageTrackers.TryRemove(Context.ConnectionId, out _);

            await Clients.All.SendAsync(
                "UpdateStatus",
                _users.Count,
                $"{user.Name} left the chat",
                _users.Select(u => u.Name)
            );
        }
    }

    public async Task SendText(string name, string message)
    {
        // 检查用户是否在冷却期
        if (!CanSendMessage(Context.ConnectionId, message))
        {
            int remainingSeconds = GetCooldownRemainingSeconds(Context.ConnectionId);
            await Clients.Caller.SendAsync("SpamProtection", remainingSeconds);
            return;
        }
        
        // 记录消息
        RecordMessage(Context.ConnectionId, message);
        
        var messageId = GenerateMessageId();
        await Clients.Caller.SendAsync("ReceiveText", name, message, "caller", messageId);
        await Clients.Others.SendAsync("ReceiveText", name, message, "others", messageId);
    }

    public async Task SendImage(string name, string url)
    {
        var messageId = GenerateMessageId();
        await Clients.Caller.SendAsync("ReceiveImage", name, url, "caller", messageId);
        await Clients.Others.SendAsync("ReceiveImage", name, url, "others", messageId);
    }

    public async Task SendYouTube(string name, string id)
    {
        var messageId = GenerateMessageId();
        await Clients.Caller.SendAsync("ReceiveYouTube", name, id, "caller", messageId);
        await Clients.Others.SendAsync("ReceiveYouTube", name, id, "others", messageId);
    }

    public async Task SendFile(string name, string url, string filename)
    {
        var messageId = GenerateMessageId();
        await Clients.Caller.SendAsync("ReceiveFile", name, url, filename, "caller", messageId);
        await Clients.Others.SendAsync("ReceiveFile", name, url, filename, "others", messageId);
    }

    public async Task SendVideo(string name, string url, string filename)
    {
        var messageId = GenerateMessageId();
        await Clients.Caller.SendAsync("ReceiveVideo", name, url, filename, "caller", messageId);
        await Clients.Others.SendAsync("ReceiveVideo", name, url, filename, "others", messageId);
    }

    public async Task DeleteMessage(string messageId)
    {
        await Clients.All.SendAsync("MessageDeleted", messageId);
    }

    public async Task UpdateMessage(string messageId, string newText)
    {
        await Clients.All.SendAsync("MessageUpdated", messageId, newText);
    }

    private string GenerateMessageId()
    {
        return $"{Context.ConnectionId}-{DateTime.Now.Ticks}";
    }
    
    #region 消息垃圾防护
    
    // 检查用户是否可以发送消息
    private bool CanSendMessage(string connectionId, string message)
    {
        if (!_messageTrackers.TryGetValue(connectionId, out var tracker))
        {
            return true;
        }
        
        // 检查用户是否在冷却期
        if (tracker.IsInCooldown())
        {
            return false;
        }
        
        // 检查是否发送了3条相同的消息
        if (tracker.CheckRepeatedMessage(message, 3))
        {
            tracker.StartCooldown(10); // 10秒冷却期
            return false;
        }
        
        // 检查10秒内是否发送了超过3条消息
        if (tracker.CheckRateLimit(3, 10))
        {
            tracker.StartCooldown(10); // 10秒冷却期
            return false;
        }
        
        return true;
    }
    
    // 记录用户发送的消息
    private void RecordMessage(string connectionId, string message)
    {
        if (_messageTrackers.TryGetValue(connectionId, out var tracker))
        {
            tracker.RecordMessage(message);
        }
    }
    
    // 获取冷却期剩余秒数
    private int GetCooldownRemainingSeconds(string connectionId)
    {
        if (_messageTrackers.TryGetValue(connectionId, out var tracker))
        {
            return tracker.GetRemainingCooldownSeconds();
        }
        return 0;
    }
    
    #endregion
}

// 用户类
public class User
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
}

// 用户消息跟踪器类
public class UserMessageTracker
{
    // 消息历史记录
    private readonly List<MessageRecord> _messageHistory = new();
    
    // 冷却期结束时间
    private DateTime _cooldownEndTime = DateTime.MinValue;
    
    // 记录消息
    public void RecordMessage(string message)
    {
        _messageHistory.Add(new MessageRecord { Message = message, Timestamp = DateTime.Now });
        
        // 清理10秒前的消息记录
        CleanupOldMessages();
    }
    
    // 检查是否在冷却期
    public bool IsInCooldown()
    {
        return DateTime.Now < _cooldownEndTime;
    }
    
    // 开始冷却期
    public void StartCooldown(int seconds)
    {
        _cooldownEndTime = DateTime.Now.AddSeconds(seconds);
    }
    
    // 获取冷却期剩余秒数
    public int GetRemainingCooldownSeconds()
    {
        if (!IsInCooldown())
        {
            return 0;
        }
        
        return (int)Math.Ceiling((_cooldownEndTime - DateTime.Now).TotalSeconds);
    }
    
    // 检查是否发送了指定数量的相同消息
    public bool CheckRepeatedMessage(string message, int count)
    {
        if (_messageHistory.Count < count)
        {
            return false;
        }
        
        // 检查最近的消息是否相同
        var recentMessages = _messageHistory
            .OrderByDescending(m => m.Timestamp)
            .Take(count)
            .ToList();
            
        return recentMessages.All(m => m.Message == message);
    }
    
    // 检查是否超过了消息发送速率限制
    public bool CheckRateLimit(int maxMessages, int seconds)
    {
        var cutoffTime = DateTime.Now.AddSeconds(-seconds);
        var recentMessagesCount = _messageHistory.Count(m => m.Timestamp >= cutoffTime);
        
        // 如果这条消息会导致超过限制，则返回true
        return recentMessagesCount >= maxMessages;
    }
    
    // 清理旧消息
    private void CleanupOldMessages()
    {
        var cutoffTime = DateTime.Now.AddSeconds(-30); // 保留30秒的消息记录
        _messageHistory.RemoveAll(m => m.Timestamp < cutoffTime);
    }
}

// 消息记录类
public class MessageRecord
{
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
}