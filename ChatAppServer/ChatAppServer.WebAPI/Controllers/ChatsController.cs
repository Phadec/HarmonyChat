﻿using ChatAppServer.WebAPI.Dtos;
using ChatAppServer.WebAPI.Models;
using ChatAppServer.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ChatAppServer.WebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Ensure all endpoints require authorization
    public sealed class ChatsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<ChatsController> _logger;

        public ChatsController(ApplicationDbContext context, IHubContext<ChatHub> hubContext, ILogger<ChatsController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }
        [HttpGet("get-relationships")]
        public async Task<IActionResult> GetRelationships(Guid userId, CancellationToken cancellationToken)
        {
            if (userId == Guid.Empty)
            {
                return BadRequest(new { Message = "Invalid userId." });
            }

            var authenticatedUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (authenticatedUserId == null || userId.ToString() != authenticatedUserId)
            {
                return Forbid("You are not authorized to view these chats.");
            }

            try
            {
                var authenticatedUserIdGuid = Guid.Parse(authenticatedUserId);

                // Get the latest message for each private contact
                var latestPrivateChats = await _context.Chats
                    .Where(c => (c.UserId == userId && c.ToUserId.HasValue) || (c.ToUserId == userId))
                    .GroupBy(c => c.UserId == userId ? c.ToUserId : c.UserId)
                    .Select(g => g.OrderByDescending(c => c.Date).FirstOrDefault())
                    .ToListAsync(cancellationToken);

                var privateChatResults = new List<object>();
                foreach (var latestChat in latestPrivateChats)
                {
                    if (latestChat != null)
                    {
                        // Determine IsSentByUser
                        bool isSentByUser = latestChat.UserId == userId;

                        // Query user information based on contactId
                        var contact = await _context.Users.FirstOrDefaultAsync(u => u.Id == (isSentByUser ? latestChat.ToUserId : latestChat.UserId), cancellationToken);

                        if (contact == null)
                        {
                            continue; // Skip this chat if the contact is not found
                        }

                        // Determine contact name and tag name
                        string contactFullName = $"{contact.FirstName} {contact.LastName}";
                        string contactTagName = contact?.TagName ?? string.Empty;

                        var friendship = await _context.Friendships.FirstOrDefaultAsync(f => (f.UserId == userId && f.FriendId == contact.Id), cancellationToken);

                        string contactNickname = friendship?.Nickname ?? string.Empty;

                        var result = new
                        {
                            RelationshipType = "Private",
                            ChatId = latestChat.Id,
                            ChatDate = latestChat.Date,
                            ContactId = isSentByUser ? latestChat.ToUserId : latestChat.UserId,
                            ContactFullName = contactFullName,
                            ContactTagName = contactTagName,
                            ContactNickname = contactNickname,
                            Status = contact.Status,
                            Avatar = contact.Avatar,
                            LastMessage = latestChat.Message ?? string.Empty,
                            LastAttachmentUrl = latestChat.AttachmentUrl ?? string.Empty,
                            IsSentByUser = isSentByUser,
                        };

                        privateChatResults.Add(result);
                    }
                }

                // Get distinct group IDs
                var groupIds = await _context.Chats
                    .Where(c => c.GroupId.HasValue && c.Group.Members.Any(m => m.UserId == userId))
                    .Select(c => c.GroupId.Value)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                // Get the latest message for each group
                var latestGroupChats = new List<object>();
                foreach (var groupId in groupIds)
                {
                    var latestChat = await _context.Chats
                        .Where(c => c.GroupId == groupId)
                        .OrderByDescending(c => c.Date)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (latestChat != null)
                    {
                        var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
                        var sender = await _context.Users.FirstOrDefaultAsync(u => u.Id == latestChat.UserId, cancellationToken);

                        if (group == null || sender == null)
                        {
                            continue; // Skip this chat if the group or sender is not found
                        }

                        latestGroupChats.Add(new
                        {
                            RelationshipType = "Group",
                            GroupId = group.Id,
                            GroupName = group.Name,
                            Avatar = group.Avatar,
                            ChatId = latestChat.Id,
                            ChatDate = latestChat.Date,
                            LastMessage = latestChat.Message ?? string.Empty,
                            LastAttachmentUrl = latestChat.AttachmentUrl ?? string.Empty,
                            IsSentByUser = latestChat.UserId == userId,
                            SenderId = latestChat.UserId,
                            SenderFullName = $"{sender.FirstName} {sender.LastName}",
                            SenderTagName = sender.TagName ?? string.Empty,
                        });
                    }
                }

                // Combine results and sort by the latest message date
                var combinedChats = privateChatResults.Concat(latestGroupChats)
                    .OrderByDescending(chat => ((dynamic)chat).ChatDate)
                    .ToList();

                return Ok(combinedChats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetRelationships for user {UserId}", userId);
                return StatusCode(500, new { Message = "Internal server error. Please try again later." });
            }
        }
        [HttpGet("{userId}/recipient-info/{recipientId}")]
        public async Task<IActionResult> GetRecipientInfo(Guid userId, Guid recipientId, CancellationToken cancellationToken)
        {
            var authenticatedUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (authenticatedUserId == null || userId.ToString() != authenticatedUserId)
            {
                return Forbid("You are not authorized to view this recipient's information.");
            }

            if (userId == Guid.Empty || recipientId == Guid.Empty)
            {
                return BadRequest("Invalid userId or recipientId.");
            }

            try
            {
                // Case 1: The recipient is the user themselves
                if (userId == recipientId)
                {
                    var user = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
                    if (user == null)
                    {
                        return NotFound("User not found.");
                    }

                    var selfInfo = new
                    {
                        Id = user.Id,
                        FullName = $"{user.FirstName} {user.LastName}",
                        Nickname = "", // No nickname for oneself
                        Avatar = user.Avatar,
                        TagName = user.TagName,
                        Status = user.Status,
                        Type = "Self"  // Indicate that this is the user's own info
                    };

                    return Ok(selfInfo);
                }

                // Case 2: The recipient is a friend
                var friendship = await _context.Friendships
                    .Include(f => f.Friend)
                    .Include(f => f.User)
                    .FirstOrDefaultAsync(f => (f.UserId == userId && f.FriendId == recipientId) ||
                                              (f.UserId == recipientId && f.FriendId == userId), cancellationToken);

                if (friendship != null)
                {
                    var recipient = friendship.UserId == userId ? friendship.Friend : friendship.User;

                    var recipientInfo = new
                    {
                        Id = recipient.Id,
                        FullName = $"{recipient.FirstName} {recipient.LastName}",
                        Nickname = friendship.Nickname,
                        Avatar = recipient.Avatar,
                        TagName = recipient.TagName,
                        Status = recipient.Status,
                        Type = "Private"  // Indicate that this is a friend
                    };

                    return Ok(recipientInfo);
                }

                // Case 3: The recipient is a group
                var group = await _context.Groups
                    .FirstOrDefaultAsync(g => g.Id == recipientId, cancellationToken);

                if (group != null)
                {
                    var groupInfo = new
                    {
                        Id = group.Id,
                        FullName = group.Name,
                        Avatar = group.Avatar,
                        Status = "group", // Special status for groups
                        TagName = "", // Groups don't have tag names
                        Nickname = "", // Groups don't have nicknames
                        Type = "Group"  // Indicate that this is a group
                    };

                    return Ok(groupInfo);
                }

                return NotFound("Recipient not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching recipient info for user {UserId} and recipient {RecipientId}.", userId, recipientId);
                return StatusCode(500, "An error occurred while processing your request.");
            }
        }
        public async Task<IActionResult> MarkAsRead(Guid chatId, CancellationToken cancellationToken)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            // Fetch the message from the database
            var message = await _context.Chats
                .FirstOrDefaultAsync(m => m.Id == chatId && m.ToUserId.ToString() == userId, cancellationToken);

            if (message == null) return NotFound("Message not found or not authorized to mark as read.");

            // Safely access nullable properties
            string messageContent = message.Message ?? "No content";
            string attachmentUrl = message.AttachmentUrl ?? string.Empty;
            string attachmentOriginalName = message.AttachmentOriginalName ?? "Unnamed file";

            // Mark the message as read
            message.IsRead = true;
            message.ReadAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            // Notify the sender that the message has been read
            await _hubContext.Clients.User(message.UserId.ToString()).SendAsync("MessageRead", message.Id);

            return Ok();
        }

        [HttpGet("get-chats")]
        public async Task<IActionResult> GetChats(Guid userId, Guid recipientId, CancellationToken cancellationToken)
        {
            if (userId == Guid.Empty || recipientId == Guid.Empty)
            {
                return BadRequest(new { Message = "Invalid userId or recipientId." });
            }

            var authenticatedUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (authenticatedUserId == null || userId.ToString() != authenticatedUserId)
            {
                return Forbid("You are not authorized to view these chats.");
            }

            var authenticatedUserIdGuid = Guid.Parse(authenticatedUserId);

            try
            {
                bool isGroup = await _context.Groups.AnyAsync(g => g.Id == recipientId, cancellationToken);

                if (!isGroup)
                {
                    // Kiểm tra xem người dùng đã bị chặn hoặc đã chặn người dùng khác
                    var isBlocked = await _context.UserBlocks
                        .AnyAsync(ub => (ub.UserId == authenticatedUserIdGuid && ub.BlockedUserId == recipientId) ||
                                        (ub.UserId == recipientId && ub.BlockedUserId == authenticatedUserIdGuid), cancellationToken);

                    if (isBlocked)
                    {
                        return Forbid("You are not authorized to view these chats.");
                    }

                    var privateChats = await _context.Chats
                        .Where(c => (c.UserId == userId && c.ToUserId == recipientId) || (c.UserId == recipientId && c.ToUserId == userId))
                        .OrderBy(c => c.Date) // Sắp xếp theo thời gian gửi tin nhắn
                        .Select(chat => new
                        {
                            chat.Id,
                            chat.UserId,
                            chat.ToUserId,
                            Message = chat.Message ?? string.Empty,
                            AttachmentUrl = chat.AttachmentUrl ?? string.Empty,
                            chat.Date,
                            isRead = chat.IsRead,
                        })
                        .ToListAsync(cancellationToken);

                    return Ok(privateChats);
                }
                else
                {
                    var isMember = await _context.GroupMembers
                        .AnyAsync(gm => gm.GroupId == recipientId && gm.UserId == authenticatedUserIdGuid, cancellationToken);

                    if (!isMember)
                    {
                        return Forbid("You are not authorized to view these group chats.");
                    }

                    var groupChats = await _context.Chats
                        .Where(p => p.GroupId == recipientId)
                        .Include(p => p.User)
                        .OrderBy(p => p.Date) // Sắp xếp theo thời gian gửi tin nhắn
                        .Select(chat => new
                        {
                            chat.Id,
                            chat.UserId,
                            SenderFullName = chat.User != null ? (chat.User.FirstName + " " + chat.User.LastName) : "Unknown",
                            SenderTagName = chat.User != null ? chat.User.TagName : "Unknown",
                            chat.GroupId,
                            Message = chat.Message ?? string.Empty,
                            AttachmentUrl = chat.AttachmentUrl ?? string.Empty,
                            chat.Date
                        })
                        .ToListAsync(cancellationToken);

                    return Ok(groupChats);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetChats for user {UserId} and recipient {RecipientId}", userId, recipientId);
                return StatusCode(500, new { Message = "Internal server error. Please try again later." });
            }
        }

        [HttpPost("send-message")]
        public async Task<IActionResult> SendMessage([FromForm] SendMessageDto request, CancellationToken cancellationToken)
        {
            if (request == null || (string.IsNullOrEmpty(request.Message) && request.Attachment == null))
            {
                return BadRequest(new { Message = "Invalid message data. Message or Attachment is required." });
            }

            if (request.UserId == Guid.Empty || request.RecipientId == Guid.Empty)
            {
                return BadRequest("Invalid userId or recipientId");
            }

            var authenticatedUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (authenticatedUserId == null || request.UserId.ToString() != authenticatedUserId)
            {
                return Forbid("You are not authorized to send this message.");
            }

            try
            {
                var sender = await _context.Users
                    .Where(u => u.Id == request.UserId)
                    .Select(u => new { u.FirstName, u.LastName })
                    .FirstOrDefaultAsync(cancellationToken);

                if (sender == null)
                {
                    return NotFound("Sender not found.");
                }

                string senderFullName = $"{sender.FirstName} {sender.LastName}";

                bool isGroup = await _context.Groups.AnyAsync(g => g.Id == request.RecipientId, cancellationToken);
                if (isGroup)
                {
                    bool isMember = await _context.GroupMembers
                        .AnyAsync(gm => gm.GroupId == request.RecipientId && gm.UserId == request.UserId, cancellationToken);

                    if (!isMember)
                    {
                        return BadRequest("You can only send messages to groups you are a member of.");
                    }

                    string? attachmentUrl = null;
                    string? originalFileName = null;
                    if (request.Attachment != null)
                    {
                        var (savedFileName, originalName) = FileService.FileSaveToServer(request.Attachment, "wwwroot/uploads/");
                        attachmentUrl = Path.Combine("uploads", savedFileName).Replace("\\", "/");
                        originalFileName = originalName;
                    }

                    Chat chat = new()
                    {
                        UserId = request.UserId,
                        GroupId = request.RecipientId,
                        Message = request.Message,
                        AttachmentUrl = attachmentUrl,
                        AttachmentOriginalName = originalFileName,
                        Date = DateTime.UtcNow
                    };

                    await _context.AddAsync(chat, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);

                    await _hubContext.Clients.Group(request.RecipientId.ToString()).SendAsync("ReceiveGroupMessage", new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.GroupId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date,
                        SenderFullName = senderFullName
                    });
                    Console.WriteLine("Message sent to user via SignalR:", request.RecipientId);
                    return Ok(new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.GroupId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date,
                        SenderFullName = senderFullName
                    });
                }
                else
                {
                    var isBlocked = await _context.UserBlocks.AnyAsync(ub => ub.UserId == request.RecipientId && ub.BlockedUserId == request.UserId, cancellationToken);
                    if (isBlocked)
                    {
                        return Forbid("You cannot send messages to this user as they have blocked you.");
                    }

                    string? attachmentUrl = null;
                    string? originalFileName = null;
                    if (request.Attachment != null)
                    {
                        var (savedFileName, originalName) = FileService.FileSaveToServer(request.Attachment, "wwwroot/uploads/");
                        attachmentUrl = Path.Combine("uploads", savedFileName).Replace("\\", "/");
                        originalFileName = originalName;
                    }

                    Chat chat = new()
                    {
                        UserId = request.UserId,
                        ToUserId = request.RecipientId,
                        Message = request.Message,
                        AttachmentUrl = attachmentUrl,
                        AttachmentOriginalName = originalFileName,
                        Date = DateTime.UtcNow
                    };

                    await _context.AddAsync(chat, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);

                    await _hubContext.Clients.User(request.RecipientId.ToString()).SendAsync("ReceivePrivateMessage", new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.ToUserId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date,
                        SenderFullName = senderFullName
                    });

                    return Ok(new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.ToUserId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date,
                        SenderFullName = senderFullName
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in SendMessage: {ex.Message}");
                return StatusCode(500, "An error occurred while processing your request.");
            }
        }


        private async Task<bool> AreFriends(Guid userId1, Guid userId2, CancellationToken cancellationToken)
        {
            return await _context.Users
                .AnyAsync(u => u.Id == userId1 && u.Friends.Any(f => f.FriendId == userId2), cancellationToken) &&
                   await _context.Users
                .AnyAsync(u => u.Id == userId2 && u.Friends.Any(f => f.FriendId == userId1), cancellationToken);
        }
    }
}