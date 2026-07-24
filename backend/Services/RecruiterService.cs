using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using backend.Data;
using backend.DTOs.Recruiter;
using backend.Models;
using backend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class RecruiterService(AppDbContext db, IEmailService emailService) : IRecruiterService
    {
        private readonly AppDbContext _db = db;
        private readonly IEmailService _emailService = emailService;

        private async Task<User> GetRecruiterOrThrowAsync(Guid recruiterId)
        {
            var recruiter = await _db.Users
                .Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Id == recruiterId)
                ?? throw new KeyNotFoundException("Recruiter account not found.");

            if (recruiter.Role != UserRole.Recruiter && recruiter.Role != UserRole.Admin)
                throw new UnauthorizedAccessException("Only Recruiters or Admins can perform these actions.");

            return recruiter;
        }

        private static bool IsSameOrganization(User recruiter, User manager)
        {
            if (recruiter.Role == UserRole.Admin) return true;

            if (recruiter.OrganizationId.HasValue && manager.OrganizationId.HasValue)
            {
                if (recruiter.OrganizationId.Value == manager.OrganizationId.Value) return true;
            }

            var recruiterOrg = recruiter.OrganizationName ?? recruiter.Organization?.Name;
            var managerOrg = manager.OrganizationName ?? manager.Organization?.Name;

            if (!string.IsNullOrWhiteSpace(recruiterOrg) && !string.IsNullOrWhiteSpace(managerOrg))
            {
                if (string.Equals(recruiterOrg.Trim(), managerOrg.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (recruiter.OrganizationId.HasValue && manager.Organization != null && manager.Organization.Id == recruiter.OrganizationId.Value)
                return true;

            return false;
        }

        public async Task<RecruiterHiringManagersResponseDto> GetHiringManagersAndInvitesAsync(Guid recruiterId)
        {
            var recruiter = await GetRecruiterOrThrowAsync(recruiterId);
            var orgName = recruiter.OrganizationName ?? recruiter.Organization?.Name ?? recruiter.FullName;

            var departments = await _db.Departments
                .Where(d => d.OrganizationName == orgName || (recruiter.OrganizationId.HasValue && d.OrganizationId == recruiter.OrganizationId.Value))
                .ToListAsync();

            var query = _db.Users
                .Include(u => u.Department)
                .Include(u => u.Organization)
                .Where(u => u.Role == UserRole.HiringManager);

            if (recruiter.OrganizationId.HasValue)
            {
                var targetOrgId = recruiter.OrganizationId.Value;
                query = query.Where(u => u.OrganizationId == targetOrgId || (u.Organization != null && u.Organization.Name == orgName));
            }
            else if (!string.IsNullOrWhiteSpace(orgName))
            {
                query = query.Where(u => u.Organization != null && u.Organization.Name == orgName);
            }

            var users = await query
                .OrderBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .ToListAsync();

            var managers = users.Select(u =>
            {
                var fullName = $"{u.FirstName} {u.LastName}".Trim();
                var dept = u.Department ?? departments.FirstOrDefault(d => !string.IsNullOrEmpty(d.Head) && string.Equals(d.Head, fullName, StringComparison.OrdinalIgnoreCase));
                return new HiringManagerDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt,
                    DepartmentId = dept?.Id,
                    DepartmentName = dept?.Name
                };
            }).ToList();

            return new RecruiterHiringManagersResponseDto
            {
                HiringManagers = managers
            };
        }

        public async Task<bool> ToggleHiringManagerStatusAsync(Guid managerId, Guid recruiterId)
        {
            var recruiter = await GetRecruiterOrThrowAsync(recruiterId);

            var manager = await _db.Users
                .Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Id == managerId)
                ?? throw new KeyNotFoundException("Hiring Manager account not found.");

            if (manager.Role != UserRole.HiringManager)
                throw new InvalidOperationException("The specified user is not a Hiring Manager.");

            if (!IsSameOrganization(recruiter, manager))
                throw new UnauthorizedAccessException("You do not have permission to manage users in other organizations.");

            manager.IsActive = !manager.IsActive;
            manager.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return manager.IsActive;
        }



        public async Task<List<BusySlotDto>> GetHiringManagerAvailabilityAsync(Guid managerId, Guid recruiterId)
        {
            var recruiter = await GetRecruiterOrThrowAsync(recruiterId);

            var manager = await _db.Users
                .Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Id == managerId)
                ?? throw new KeyNotFoundException("Hiring Manager account not found.");

            if (manager.Role != UserRole.HiringManager)
                throw new InvalidOperationException("The specified user is not a Hiring Manager.");

            if (!IsSameOrganization(recruiter, manager))
                throw new UnauthorizedAccessException("You do not have permission to view users in other organizations.");

            var managerFullName = $"{manager.FirstName} {manager.LastName}".Trim();

            var allInterviews = await _db.Interviews
                .AsNoTracking()
                .Where(i => i.ScheduledAt >= DateTime.UtcNow.AddDays(-1))
                .ToListAsync();

            var busySlots = allInterviews
                .Where(i => i.InterviewerName.Trim().Equals(managerFullName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.ScheduledAt)
                .Select(i => new BusySlotDto
                {
                    ScheduledAt = DateTime.SpecifyKind(i.ScheduledAt, DateTimeKind.Utc),
                    DurationMinutes = i.DurationMinutes
                })
                .ToList();

            return busySlots;
        }

        public async Task DeleteHiringManagerAsync(Guid managerId, Guid recruiterId)
        {
            var recruiter = await GetRecruiterOrThrowAsync(recruiterId);

            var manager = await _db.Users
                .Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Id == managerId)
                ?? throw new KeyNotFoundException("Hiring Manager account not found.");

            if (manager.Role != UserRole.HiringManager)
                throw new InvalidOperationException("The specified user is not a Hiring Manager.");

            if (!IsSameOrganization(recruiter, manager))
                throw new UnauthorizedAccessException("You do not have permission to manage users in other organizations.");

            // Unset from any departments they head
            var managerFullName = $"{manager.FirstName} {manager.LastName}".Trim();
            var orgName = recruiter.OrganizationName ?? recruiter.Organization?.Name ?? recruiter.FullName;
            var departments = await _db.Departments
                .Where(d => d.Head == managerFullName && d.OrganizationName == orgName)
                .ToListAsync();

            foreach (var dept in departments)
            {
                dept.Head = string.Empty;
                dept.HeadInitials = string.Empty;
            }

            _db.Users.Remove(manager);
            await _db.SaveChangesAsync();
        }
    }
}
