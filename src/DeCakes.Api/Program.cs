using DeCakes.Api.Authentication;
using DeCakes.Application;
using DeCakes.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options => options.Filters.Add<AntiforgeryValidationFilter>());
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddDeCakesAuthentication(builder.Configuration);

var app = builder.Build();

// Mọi môi trường: lỗi không xử lý trả ProblemDetails 500, không lộ chi tiết (kể cả lỗi database).
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
