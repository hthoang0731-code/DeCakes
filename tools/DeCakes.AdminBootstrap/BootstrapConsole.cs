using System.Text;
using DeCakes.Application.Admin;

namespace DeCakes.AdminBootstrap;

public interface IOperatorPrompt
{
    bool IsInteractive { get; }
    string Read(string prompt);
    string ReadHidden(string prompt);
    void Write(string message);
}

public static class BootstrapConsole
{
    public static async Task<int> RunAsync(string[] args, IOperatorPrompt prompt, Func<AdminBootstrapService> createService)
    {
        if (args.Length != 0 || !prompt.IsInteractive)
        {
            prompt.Write("Cần terminal tương tác; không chấp nhận tham số hoặc đầu vào chuyển hướng.");
            return 1;
        }

        try
        {
            var email = prompt.Read("Email admin mới: ");
            var name = prompt.Read("Họ tên: ");
            var password = prompt.ReadHidden("Mật khẩu (12–128 ký tự, nhập ẩn): ");
            var confirm = prompt.ReadHidden("Nhập lại mật khẩu: ");
            if (password != confirm)
            {
                prompt.Write("Mật khẩu xác nhận không khớp.");
                return 1;
            }

            var outcome = await createService().CreateAsync(email, name, password, CancellationToken.None);
            prompt.Write(outcome switch
            {
                AdminBootstrapOutcome.Created => "Đã tạo admin mới (active, không demo).",
                AdminBootstrapOutcome.EmailTaken => "Email đã tồn tại. Không thay đổi tài khoản hiện có.",
                _ => "Dữ liệu không hợp lệ. Không tạo tài khoản.",
            });
            return outcome == AdminBootstrapOutcome.Created ? 0 : 1;
        }
        catch (Exception)
        {
            // Never echo exception details, connection strings, hashes or input values.
            prompt.Write("Không tạo được admin. Kiểm tra cấu hình kết nối và quyền INSERT users bằng kênh vận hành an toàn.");
            return 1;
        }
    }
}

public sealed class OperatorPrompt : IOperatorPrompt
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public string Read(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    public string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && value.Length < 129) value.Append(key.KeyChar);
        }

        Console.WriteLine();
        return value.ToString();
    }

    public void Write(string message) => Console.WriteLine(message);
}
