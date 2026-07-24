using System;
using System.Collections.Generic;

namespace backend.DTOs.Recruiter
{
    public class HiringManagerDto
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
    }

    public class RecruiterHiringManagersResponseDto
    {
        public List<HiringManagerDto> HiringManagers { get; set; } = [];
    }

    public class BusySlotDto
    {
        public DateTime ScheduledAt { get; set; }
        public int DurationMinutes { get; set; }
    }
}
