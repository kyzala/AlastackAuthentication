using Alastack.Authentication.Hawk.AspNetCore;
using Alastack.Authentication.Hmac.AspNetCore;
using Alastack.Authentication.MongoDB;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddControllers();
builder.Services.AddAuthentication()
.AddHawk(options =>
{
    options.CredentialProvider = new MongoDBCredentialProvider<HawkCredential>("mongodb://localhost:27017", "credentials", "hawk", "authId");
})
.AddHmac(options =>
{
    options.CredentialProvider = new MongoDBCredentialProvider<HmacCredential>("mongodb://localhost:27017", "credentials", "hmac", "appId");
});

var app = builder.Build();

app.UseAuthentication();
app.UseHawkServerAuthorization();
app.UseAuthorization();
app.MapControllers();
app.Run();
