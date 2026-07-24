using backend.Data;
using backend.DTOs.Admin;
using backend.DTOs.Auth;
using backend.Models;
using backend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class AdminUserService(
        AppDbContext dbContext,
        IEmailService emailService,
        IAuditLogService auditLogService) : IAdminUserService
    {
        private readonly AppDbContext _db = dbContext;
        private readonly IEmailService _emailService = emailService;
        private readonly IAuditLogService _auditLogService = auditLogService;

        public async Task<UserListDto> CreateUserAsync(CreateUserDto dto)
        {
            if (dto.Role != UserRole.Recruiter && dto.Role != UserRole.HiringManager)
                throw new ArgumentException("Admin can only create Recruiter or HiringManager accounts.");

            var emailExists = await _db.Users.AnyAsync(u => u.Email.ToLower() == dto.Email.ToLower());
            if (emailExists)
                throw new InvalidOperationException($"A user with email '{dto.Email}' already exists.");

            var user = new User
            {
                FirstName = dto.FirstName.Trim(),
                LastName = dto.LastName.Trim(),
                Email = dto.Email.ToLower().Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                Role = dto.Role,
                Status = UserStatus.Active,
                OrganizationId = dto.OrganizationId,
                DepartmentId = dto.DepartmentId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            await _auditLogService.LogActivityAsync(
                action: "USER_CREATED",
                module: "Users",
                details: new { userId = user.Id, email = user.Email, role = user.Role.ToString() });

            // Load navigation properties for mapping
            if (user.OrganizationId.HasValue)
                await _db.Entry(user).Reference(u => u.Organization).LoadAsync();
            if (user.DepartmentId.HasValue)
                await _db.Entry(user).Reference(u => u.Department).LoadAsync();

            return MapToListDto(user);
        }

        public async Task<PagedResultDto<UserListDto>> GetAllUsersAsync(UserRole? roleFilter, int page, int pageSize)
        {
            var query = _db.Users
                .Include(u => u.Organization)
                .Include(u => u.Department)
                .AsQueryable();

            if (roleFilter.HasValue)
                query = query.Where(u => u.Role == roleFilter.Value);

            var totalCount = await query.CountAsync();

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var items = users.Select(MapToListDto).ToList();

            return new PagedResultDto<UserListDto>
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            };
        }

        public async Task<UserListDto?> GetUserByIdAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            return user is null ? null : MapToListDto(user);
        }

        public async Task<UserListDto> UpdateUserAsync(Guid id, UpdateUserDto dto)
        {
            var user = await _db.Users
                .Include(u => u.Organization)
                .Include(u => u.Department)
                .FirstOrDefaultAsync(u => u.Id == id)
                ?? throw new KeyNotFoundException($"User with ID '{id}' not found.");

            // Prevent editing another admin's core identity fields
            if (user.Role == UserRole.Admin)
                throw new InvalidOperationException("Admin accounts cannot be edited via this endpoint.");

            // Check email uniqueness if changed
            var emailLower = dto.Email.ToLower().Trim();
            if (!string.Equals(user.Email, emailLower, StringComparison.OrdinalIgnoreCase))
            {
                var emailExists = await _db.Users.AnyAsync(u => u.Email.ToLower() == emailLower && u.Id != id);
                if (emailExists)
                    throw new InvalidOperationException($"A user with email '{dto.Email}' already exists.");
            }

            user.FirstName = dto.FirstName.Trim();
            user.LastName = dto.LastName.Trim();
            user.Email = emailLower;

            if (dto.Role.HasValue)
                user.Role = dto.Role.Value;

            if (dto.Status.HasValue)
            {
                user.Status = dto.Status.Value;
                user.IsActive = dto.Status.Value == UserStatus.Active;
            }

            // Only reassign org/dept when explicitly provided; keep existing when null
            if (dto.OrganizationId.HasValue)
                user.OrganizationId = dto.OrganizationId;

            if (dto.DepartmentId.HasValue)
                user.DepartmentId = dto.DepartmentId;
            user.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _auditLogService.LogActivityAsync(
                action: "USER_UPDATED",
                module: "Users",
                details: new { userId = user.Id, email = user.Email, role = user.Role.ToString() });

            // Reload navigation props
            await _db.Entry(user).Reference(u => u.Organization).LoadAsync();
            await _db.Entry(user).Reference(u => u.Department).LoadAsync();

            return MapToListDto(user);
        }

        public async Task DeleteUserAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id)
                ?? throw new KeyNotFoundException($"User with ID '{id}' not found.");

            if (user.Role == UserRole.Admin)
                throw new InvalidOperationException("Admin accounts cannot be deleted.");

            _db.Users.Remove(user);
            await _db.SaveChangesAsync();

            await _auditLogService.LogActivityAsync(
                action: "USER_DELETED",
                module: "Users",
                details: new { userId = user.Id, email = user.Email, role = user.Role.ToString() });
        }

        public async Task<bool> ToggleUserActiveAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id)
                ?? throw new KeyNotFoundException($"User with ID '{id}' not found.");

            if (user.Role == UserRole.Admin)
                throw new InvalidOperationException("Admin accounts cannot be deactivated.");

            user.IsActive = !user.IsActive;
            user.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _auditLogService.LogActivityAsync(
                action: user.IsActive ? "USER_ACTIVATED" : "USER_DEACTIVATED",
                module: "Users",
                details: new { userId = user.Id, email = user.Email });

            return user.IsActive;
        }

        public async Task ResetUserPasswordAsync(Guid id, string newPassword)
        {
            var user = await _db.Users.FindAsync(id)
                ?? throw new KeyNotFoundException($"User with ID '{id}' not found.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _auditLogService.LogActivityAsync(
                action: "USER_PASSWORD_RESET",
                module: "Users",
                details: new { userId = user.Id, email = user.Email });
        }



        private static UserListDto MapToListDto(User u) => new()
        {
            Id = u.Id,
            FullName = $"{u.FirstName} {u.LastName}",
            Email = u.Email,
            Role = u.Role.ToString(),
            Status = u.Status.ToString(),
            OrganizationName = u.Organization?.Name ?? u.OrganizationName,
            OrganizationId = u.OrganizationId,
            DepartmentName = u.Department?.Name,
            DepartmentId = u.DepartmentId,
            IsActive = u.IsActive,
            CreatedAt = u.CreatedAt
        };
    }
}
