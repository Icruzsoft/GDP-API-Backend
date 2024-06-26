using GDP_API.Models;
using GDP_API.Models.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;


public class UserService : IUserService
{
    private readonly IUserRepository _repository;
    private readonly IExpertUserRepository _expertRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserService> _logger;
    private readonly IEmailService _emailService;
    public UserService(IUserRepository repository,
    IConfiguration configuration, ILogger<UserService> logger,
     IExpertUserRepository expertRepository, IEmailService emailService)
    {
        _repository = repository;
        _configuration = configuration;
        _logger = logger;
        _expertRepository = expertRepository;
        _emailService = emailService;
    }

    /// <summary>
    /// Retrieves all users from the repository.
    /// </summary>
    /// <returns>A list of User objects.</returns>
    public async Task<List<User>> GetAllUsers()
    {
        return await _repository.GetAllUsers();
    }

    /// <summary>
    /// Retrieves a user by their ID.
    /// </summary>
    /// <param name="id">The ID of the user to retrieve.</param>
    /// <returns>The user with the specified ID.</returns>
    public async Task<User> GetUser(int id)
    {
        return await _repository.GetUser(id);
    }
    public async Task<User> Register(UserRegistrationDTO registrationDTO)
    {
        try
        {
            // Check if a user with the same email already exists
            var existingUser = await _repository.GetUserByEmail(registrationDTO.Email);
            if (existingUser != null)
            {
                throw new Exception("An account with this email already exists.");
            }
            string passwordHash = BCrypt.Net.BCrypt.HashPassword(registrationDTO.Password);
            string token = Guid.NewGuid().ToString();
            string tokenHash = BCrypt.Net.BCrypt.HashPassword(token);
            User user = new User
            {
                Name = registrationDTO.Name,
                Surname = registrationDTO.Surname,
                Email = registrationDTO.Email,
                PhoneNumber = registrationDTO.PhoneNumber,
                Password = passwordHash,
                AddressLine1 = registrationDTO.AddressLine1,
                AddressLine2 = registrationDTO.AddressLine2,
                ZipCode = registrationDTO.ZipCode,
                City = registrationDTO.City,
                State = registrationDTO.State,
                Country = registrationDTO.Country,
                TermsAccepted = registrationDTO.TermsAccepted,
                Token = tokenHash,
                CreatedDate = DateTime.UtcNow,
                UserTypeId = registrationDTO.UserTypeId
            };

            await _emailService.SendEmailAsync($"{user.Email}", "Welcome to GDP",
                CreateConfirmationEmailBody(user.Name, user.Email, token));
            return await _repository.AddUser(user);
        }
        catch (DbUpdateException ex)
        {
            if (ex.InnerException != null && ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627))
            {
                throw new Exception("An account with this email already exists.");
            }
            throw;
        }
    }

    public async Task<ExpertUser> RegisterExpert(UserRegistrationDTO registrationDTO)
    {
        try
        {

            var user = await Register(registrationDTO);
            ExpertUser expert = new ExpertUser
            {
                UserId = user.Id,
                User = user,
                Experience = registrationDTO.Experience ?? "",
                EducationLevel = registrationDTO.EducationLevel ?? "",
                CVPath = registrationDTO.CVPath ?? "",
                LinkedInURI = registrationDTO.LinkedInURI ?? ""
            };

            return await _expertRepository.Add(expert);
        }
        catch (Exception)
        {
            throw;
        }

    }
    public async Task<string> Login(string email, string password, bool otp = false)
    {
        var user = await _repository.GetUserByEmail(email);

        if (user == null)
        {
            return null;
        }
        if (!CheckHash(user, password, otp))
        {
            throw new Exception("Invalid credentials");
        }
        if (!user.Confirmed)
        {
            throw new Exception("Email not confirmed");
        }
        var tokenHandler = new JwtSecurityTokenHandler();
        //TODO - Use Azure keystore for production
        var key = Encoding.UTF8.GetBytes(_configuration.GetSection("AppSettings:Token").Value!);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] {
                 new Claim(ClaimTypes.Name, user.Id.ToString()),
                 new Claim(ClaimTypes.Role, user.UserTypeId.ToString()) }),
            Expires = DateTime.UtcNow.AddDays(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        if (otp)
        {
            await _repository.ResetOtp(user);
        }
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
    public async Task<User> GetUserByEmail(string email)
    {
        return await _repository.GetUserByEmail(email);
    }
    public async Task ConfirmEmail(string email, string token)
    {
        var user = await _repository.GetUserByEmail(email);

        if (user != null)
        {

            if (user.Confirmed)
            {
                throw new Exception("Email already confirmed");
            }
            if (BCrypt.Net.BCrypt.Verify(token, user.Token))
            {
                await _repository.ConfirmEmail(user);
                return;
            }
        }
        if (user == null)
        {
            throw new Exception("User not found");

        }
        throw new Exception("Invalid token");

    }
    public async Task<string> RequestOtp(string email, bool isReset)
    {
        var user = await _repository.GetUserByEmail(email);
        if (user == null)
        {
            throw new Exception("User not found");
        }
        if (user.Confirmed)
        {
            var otpValue = new Random().Next(100000, 999999).ToString();
            var otp = BCrypt.Net.BCrypt.HashPassword(otpValue);
            await _repository.SetOtp(user, otp);
            await _emailService.SendEmailAsync($"{user.Email}", "GDP - OTP",
                CreateOTPEmailBody(user.Name, otpValue, isReset));
            return $"OTP sent to {user.Email}";
        }
        throw new Exception("User not confirmed");
    }
    private bool CheckHash(User user, string password, bool otp = false)
    {
        if (otp && string.IsNullOrEmpty(user.Token))
        {
            return false;
        }
        return !otp ? BCrypt.Net.BCrypt.Verify(password, user.Password)
        : BCrypt.Net.BCrypt.Verify(password, user.Token);
    }
    public async Task ResetPassword(string jwt, string newPassword, string confirm)
    {
        if (newPassword != confirm)
        {
            throw new Exception("Passwords do not match");
        }
        // Extract the user ID from the JWT
        var tokenHandler = new JwtSecurityTokenHandler();
        var jwtToken = tokenHandler.ReadJwtToken(jwt);
        //TODO - Research if claim type can be changed to use a .net constant instead of a string
        //  ClaimTypes.NameIdentifier and  ClaimTypes.Name do not work
        int userIdFromJwt = int.Parse(jwtToken.Claims.First(claim => claim.Type == "unique_name").Value);

        // Get the user from the database
        var user = await _repository.GetUser(userIdFromJwt);

        if (user == null)
        {
            throw new Exception("User not found.");
        }
        string passwordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        // Reset the password
        await _repository.ResetPassword(user, passwordHash);
    }

    private string CreateConfirmationEmailBody(string userName, string email, string token)
    {
        string BackEndURL = _configuration.GetSection("AppSettings:BackEndURL").Value!;
        var confirmationLink = $"{BackEndURL}/api/User/email/confirm?email={email}&token={token}";

        var emailTemplatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "ConfirmationEmail.html");
        var emailBody = File.ReadAllText(emailTemplatePath);

        emailBody = emailBody.Replace("{USERNAME}", userName);
        emailBody = emailBody.Replace("{CONFIRMATION_LINK}", confirmationLink);

        return emailBody;
    }
    private string CreateOTPEmailBody(string userName, string otp, bool isReset = false)
    {
        string emailTemplatePath = isReset ? Path.Combine(Directory.GetCurrentDirectory(), "Templates", "PasswordReset.html")
        : Path.Combine(Directory.GetCurrentDirectory(), "Templates", "OTPEmail.html");
        var emailBody = File.ReadAllText(emailTemplatePath);

        emailBody = emailBody.Replace("{USERNAME}", userName);
        emailBody = emailBody.Replace("{OTP}", otp);

        return emailBody;
    }

}