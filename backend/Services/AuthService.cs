using backend.Data;
using backend.DTOs.Auth;
using backend.Models;
using backend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class AuthService(
        AppDbContext dbContext,
        IJwtService jwtService,
        IEmailService emailService,
        IAuditLogService auditLogService) : IAuthService
    {
        private readonly AppDbContext _db = dbContext;
        private readonly IJwtService _jwtService = jwtService;
        private readonly IEmailService _emailService = emailService;
        private readonly IAuditLogService _auditLogService = auditLogService;

        // ─────────────────────────────────────────────
        // Candidate Registration
        // ─────────────────────────────────────────────
        public async Task<AuthResponseDto> RegisterCandidateAsync(RegisterCandidateDto dto)
        {
            var emailExists = await _db.Users.AnyAsync(u => u.Email.ToLower() == dto.Email.ToLower());
            if (emailExists)
                throw new InvalidOperationException("An account with this email already exists.");

            var user = new User
            {
                FirstName = dto.FirstName.Trim(),
                LastName = dto.LastName.Trim(),
                Email = dto.Email.ToLower().Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                Role = UserRole.Candidate,
                Status = UserStatus.Active,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);

            var profile = new CandidateProfile
            {
                UserId = user.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.CandidateProfiles.Add(profile);

            await _db.SaveChangesAsync();

            // Send welcome email to candidate
            var emailSubject = $"Welcome to Wayfare Global, {user.FirstName}!";
            var emailBody = $@"
                <div style='font-family: sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #e2e8f0; border-radius: 12px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1);'>
                    <h2 style='color: #4f46e5; margin-bottom: 16px;'>Welcome to Wayfare Global!</h2>
                    <p style='color: #475569; font-size: 16px; line-height: 1.6;'>Hello {user.FirstName},</p>
                    <p style='color: #475569; font-size: 16px; line-height: 1.6;'>Thank you for registering an account on our platform! We are excited to help you find your next role using AI-powered matching.</p>
                    <p style='color: #475569; font-size: 16px; line-height: 1.6;'>To get started, log in to your candidate dashboard to build your profile, upload your resume, and start matching with top jobs:</p>
                    <p style='margin: 24px 0; text-align: center;'>
                        <a href='http://localhost:5173/dashboard' style='background-color: #4f46e5; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 8px; font-weight: 600; display: inline-block;'>Go to Candidate Dashboard</a>
                    </p>
                    <hr style='border: 0; border-top: 1px solid #e2e8f0; margin: 24px 0;' />
                    <p style='color: #94a3b8; font-size: 12px;'>This is an automated notification. Please do not reply directly to this email.</p>
                </div>";

            try
            {
                await _emailService.SendEmailAsync(user.Email, emailSubject, emailBody);
            }
            catch
            {
                // Silently swallow email failures in background so registration flow succeeds
            }

            var token = _jwtService.GenerateToken(user);
            return BuildAuthResponse(user, token);
        }

        // ─────────────────────────────────────────────
        // Login
        // ─────────────────────────────────────────────
        public async Task<AuthResponseDto> LoginAsync(LoginDto dto)
        {
            var user = await _db.Users
                .Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == dto.Email.ToLower());

            if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
                throw new UnauthorizedAccessException("Invalid email or password.");

            // Check recruiter pending status
            if (user.Status == UserStatus.Pending)
                throw new InvalidOperationException("Your account is awaiting admin approval. You will receive access once an administrator reviews your application.");

            // Check recruiter rejected status
            if (user.Status == UserStatus.Rejected)
                throw new UnauthorizedAccessException("Your account application was not approved. Please contact support for more information.");

            if (!user.IsActive)
                throw new UnauthorizedAccessException("Your account has been deactivated. Please contact the administrator.");

            var token = _jwtService.GenerateToken(user);
            return BuildAuthResponse(user, token);
        }

        // ─────────────────────────────────────────────
        // Helper
        // ─────────────────────────────────────────────
        private AuthResponseDto BuildAuthResponse(User user, string token) => new()
        {
            Token = token,
            UserId = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role.ToString(),
            Status = user.Status.ToString(),
            OrganizationName = user.Organization?.Name ?? user.OrganizationName,
            ExpiresAt = _jwtService.GetExpiry()
        };
    }
}
