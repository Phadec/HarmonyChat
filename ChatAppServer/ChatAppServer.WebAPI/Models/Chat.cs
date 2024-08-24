﻿namespace ChatAppServer.WebAPI.Models
{
    public sealed class Chat
    {
        public Chat()
        {
            Id = Guid.NewGuid();
            Date = DateTime.UtcNow;
        }

        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; }
        public Guid? ToUserId { get; set; }
        public User? ToUser { get; set; } // Cho phép null
        public Guid? GroupId { get; set; }
        public Group? Group { get; set; } // Cho phép null
        public string? Message { get; set; } // Cho phép null
        public string? AttachmentUrl { get; set; } // Cho phép null
        public string? AttachmentOriginalName { get; set; } // Cho phép null
        public DateTime Date { get; set; }

        // Thuộc tính mới để lưu trạng thái đã đọc
        public bool IsRead { get; set; } = false; // Mặc định là chưa đọc
        public DateTime? ReadAt { get; set; } // Thời gian đã đọc (nullable)

        // Thuộc tính mới để theo dõi trạng thái xóa tin nhắn
        public bool IsDeleted { get; set; } = false; // Mặc định là chưa bị xóa

        public ICollection<Reaction> Reactions { get; set; } = new List<Reaction>();
    }
}
