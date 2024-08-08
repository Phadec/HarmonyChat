﻿using ChatAppServer.WebAPI.Dtos;
using ChatAppServer.WebAPI.Hubs;
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
                return BadRequest("Invalid userId");
            }

            var authenticatedUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (authenticatedUserId == null || userId.ToString() != authenticatedUserId)
            {
                return Forbid("You are not authorized to view these chats.");
            }

            try
            {
                var authenticatedUserIdGuid = Guid.Parse(authenticatedUserId);

                // Lấy danh sách các liên hệ riêng tư (ToUserId) không trùng lặp
                var privateContactIds = await _context.Chats
                    .Where(c => (c.UserId == userId && c.ToUserId.HasValue) || (c.ToUserId == userId))
                    .Select(c => c.UserId == userId ? c.ToUserId.Value : c.UserId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                // Lấy danh sách các GroupId không trùng lặp
                var groupIds = await _context.Chats
                    .Where(c => c.GroupId.HasValue && c.Group.Members.Any(m => m.UserId == userId))
                    .Select(c => c.GroupId.Value)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                // Lấy tin nhắn mới nhất từ mỗi liên hệ riêng tư
                var latestPrivateChats = new List<object>();
                foreach (var contactId in privateContactIds)
                {
                    var latestChat = await _context.Chats
                        .Where(c => (c.UserId == userId && c.ToUserId == contactId) || (c.ToUserId == userId && c.UserId == contactId))
                        .OrderByDescending(c => c.Date)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (latestChat != null)
                    {
                        var recipient = latestChat.ToUserId == userId ? latestChat.User : await _context.Users.FirstOrDefaultAsync(u => u.Id == latestChat.ToUserId, cancellationToken);
                        latestPrivateChats.Add(new
                        {
                            ChatId = latestChat.Id,
                            ChatDate = latestChat.Date,
                            IsGroup = false,
                            RecipientId = recipient.Id,
                            RecipientName = recipient.Username,
                            LastMessage = latestChat.Message ?? string.Empty,
                            LastAttachmentUrl = latestChat.AttachmentUrl ?? string.Empty,
                            IsSentByUser = latestChat.UserId == userId,
                            SenderId = latestChat.UserId,
                            SenderName = latestChat.User.Username
                        });
                    }
                }

                // Lấy tin nhắn mới nhất từ mỗi nhóm
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
                        latestGroupChats.Add(new
                        {
                            ChatId = latestChat.Id,
                            ChatDate = latestChat.Date,
                            IsGroup = true,
                            RecipientId = group.Id,
                            RecipientName = group.Name,
                            LastMessage = latestChat.Message ?? string.Empty,
                            LastAttachmentUrl = latestChat.AttachmentUrl ?? string.Empty,
                            IsSentByUser = latestChat.UserId == userId,
                            SenderId = latestChat.UserId,
                            SenderName = latestChat.User.Username
                        });
                    }
                }

                // Kết hợp các kết quả và sắp xếp theo thời gian
                var combinedChats = latestPrivateChats.Concat(latestGroupChats)
                    .OrderByDescending(chat => ((dynamic)chat).ChatDate)
                    .ToList();

                return Ok(combinedChats);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetRelationships: {ex.Message}");
                return StatusCode(500, "Internal server error");
            }
        }


        [HttpGet("get-chats")]
        public async Task<IActionResult> GetChats(Guid userId, Guid recipientId, CancellationToken cancellationToken)
        {
            if (userId == Guid.Empty || recipientId == Guid.Empty)
            {
                return BadRequest("Invalid userId or recipientId");
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
                    // Kiểm tra xem người dùng đã bị chặn hay đã chặn người dùng khác
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
                            chat.Date
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
                            Username = chat.User.Username,
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
                _logger.LogError($"Error in GetChats: {ex.Message}");
                return StatusCode(500, "Internal server error");
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
                // Kiểm tra nếu là tin nhắn nhóm
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

                    await _hubContext.Clients.All.SendAsync("ReceiveGroupMessage", new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.GroupId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date
                    });

                    return Ok(new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.GroupId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date
                    });
                }
                // Xử lý tin nhắn riêng tư
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

                    await _hubContext.Clients.All.SendAsync("ReceivePrivateMessage", new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.ToUserId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date
                    });

                    return Ok(new
                    {
                        chat.Id,
                        chat.UserId,
                        chat.ToUserId,
                        chat.Message,
                        chat.AttachmentUrl,
                        chat.AttachmentOriginalName,
                        chat.Date
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
