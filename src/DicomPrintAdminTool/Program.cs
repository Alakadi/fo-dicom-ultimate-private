using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ============================================================
// DICOM Print Server — Admin Tool (M6-A / M6-G)
// ============================================================
// الاستخدام:
//   admintool keygen           — يولّد زوج مفاتيح RSA جديد
//   admintool issue            — ينشئ ترخيصاً تفاعلياً
//   admintool verify <file>    — يتحقق من ملف ترخيص
//   admintool info <file>      — يعرض معلومات الترخيص
// ============================================================

const string Version = "1.0.0";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine($"║  DICOM Print Server — Admin Tool v{Version}   ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

if (args.Length == 0)
{
    ShowHelp();
    return 0;
}

string command = args[0].ToLowerInvariant();

return command switch
{
    "keygen"       => DoKeyGen(),
    "issue"        => DoIssue(args),
    "verify"       => DoVerify(args),
    "info"         => DoInfo(args),
    "hash"         => DoHashPassword(args),
    "help"         => DoHelp(),
    _              => DoUnknown(command)
};

// ════════════════════════════════════════════════════════════
// keygen — توليد زوج مفاتيح RSA
// ════════════════════════════════════════════════════════════
static int DoKeyGen()
{
    Console.WriteLine("Generating RSA 2048-bit key pair...");
    Console.WriteLine();

    using var rsa = RSA.Create(2048);
    string privatePem = rsa.ExportRSAPrivateKeyPem();
    string publicPem  = rsa.ExportSubjectPublicKeyInfoPem();

    string privateFile = "private_key.pem";
    string publicFile  = "public_key.pem";

    File.WriteAllText(privateFile, privatePem, Encoding.UTF8);
    File.WriteAllText(publicFile,  publicPem,  Encoding.UTF8);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"✅ Private key saved: {Path.GetFullPath(privateFile)}");
    Console.WriteLine($"✅ Public key saved:  {Path.GetFullPath(publicFile)}");
    Console.ResetColor();
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠️  Keep private_key.pem SECURE — never share it.");
    Console.WriteLine("    Embed public_key.pem in LicenseManager.cs (PublicKeyPem constant).");
    Console.ResetColor();

    return 0;
}

// ════════════════════════════════════════════════════════════
// issue — إنشاء ترخيص جديد (تفاعلي)
// ════════════════════════════════════════════════════════════
static int DoIssue(string[] args)
{
    string privateKeyFile = args.Length > 1 ? args[1] : "private_key.pem";

    if (!File.Exists(privateKeyFile))
    {
        Console.Error.WriteLine($"Private key not found: {privateKeyFile}");
        Console.Error.WriteLine("Run 'admintool keygen' first.");
        return 1;
    }

    string privateKeyPem = File.ReadAllText(privateKeyFile, Encoding.UTF8);

    Console.WriteLine("═══ License Generator ════════════════════════");
    Console.WriteLine();

    string customerId   = Prompt("Customer ID (e.g. HOSP-001):", "HOSP-001");
    string customerName = Prompt("Customer Name (Arabic/English):", "مستشفى الملك فهد");
    string licType      = PromptChoice("License Type:", new[] { "Full", "Trial" }, "Full");
    int    maxPorts     = int.Parse(Prompt("Max Ports:", "4"));
    string expStr       = Prompt("Expiry Date (YYYY-MM-DD, blank = never):", "");

    DateTime? expiresAt = null;
    if (!string.IsNullOrWhiteSpace(expStr) && DateTime.TryParse(expStr, out var exp))
        expiresAt = exp.ToUniversalTime();

    Console.WriteLine("Features (comma-separated, blank = ALL):");
    Console.WriteLine("  Available: JPG, PDF, WhatsApp, MultiPort, Calibration");
    string featStr   = Prompt("Features:", "");
    List<string>? features = string.IsNullOrWhiteSpace(featStr)
        ? null
        : featStr.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

    Console.WriteLine();
    Console.WriteLine("═══ Summary ══════════════════════════════════");
    Console.WriteLine($"  Customer ID:   {customerId}");
    Console.WriteLine($"  Customer Name: {customerName}");
    Console.WriteLine($"  License Type:  {licType}");
    Console.WriteLine($"  Max Ports:     {maxPorts}");
    Console.WriteLine($"  Expires:       {(expiresAt.HasValue ? expiresAt.Value.ToString("yyyy-MM-dd") : "Never")}");
    Console.WriteLine($"  Features:      {(features == null ? "ALL" : string.Join(", ", features))}");
    Console.WriteLine();

    string confirm = Prompt("Confirm? (yes/no):", "yes");
    if (!confirm.Equals("yes", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Cancelled.");
        return 0;
    }

    // إنشاء الترخيص
    var licenseData = new LicenseData
    {
        CustomerId   = customerId,
        CustomerName = customerName,
        LicenseType  = licType,
        IssuedAt     = DateTime.UtcNow,
        ExpiresAt    = expiresAt,
        MaxPorts     = maxPorts,
        Features     = features
    };

    string signedJson = CreateSignedLicense(licenseData, privateKeyPem);

    string outFile = $"license_{customerId.Replace("-", "").ToLower()}.key";
    File.WriteAllText(outFile, signedJson, Encoding.UTF8);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n✅ License saved: {Path.GetFullPath(outFile)}");
    Console.ResetColor();
    Console.WriteLine("\nSend this file to the customer — place it at:");
    Console.WriteLine("  • Next to DicomPrintServer.exe  (as license.key)");
    Console.WriteLine("  • OR: %ProgramData%\\DicomPrintServer\\license.key");

    return 0;
}

// ════════════════════════════════════════════════════════════
// verify — التحقق من ملف ترخيص
// ════════════════════════════════════════════════════════════
static int DoVerify(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Usage: verify <license-file> [public-key.pem]"); return 1; }

    string licFile    = args[1];
    string pubKeyFile = args.Length > 2 ? args[2] : "public_key.pem";

    if (!File.Exists(licFile))   { Console.Error.WriteLine($"File not found: {licFile}");   return 1; }
    if (!File.Exists(pubKeyFile)){ Console.Error.WriteLine($"Key not found: {pubKeyFile}"); return 1; }

    string licJson    = File.ReadAllText(licFile, Encoding.UTF8);
    string publicPem  = File.ReadAllText(pubKeyFile, Encoding.UTF8);

    bool valid = VerifyLicense(licJson, publicPem);

    if (valid)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ License signature is VALID");
        Console.ResetColor();
        DoInfo(args);
        return 0;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("❌ License signature is INVALID");
        Console.ResetColor();
        return 1;
    }
}

// ════════════════════════════════════════════════════════════
// info — عرض معلومات الترخيص
// ════════════════════════════════════════════════════════════
static int DoInfo(string[] args)
{
    string licFile = args.Length > 1 ? args[1] : "license.key";
    if (!File.Exists(licFile)) { Console.Error.WriteLine($"File not found: {licFile}"); return 1; }

    string json = File.ReadAllText(licFile, Encoding.UTF8);

    try
    {
        var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Console.WriteLine("\n═══ License Information ══════════════════════");
        PrintField("Customer ID",   root, "CustomerId");
        PrintField("Customer Name", root, "CustomerName");
        PrintField("License Type",  root, "LicenseType");
        PrintField("Issued At",     root, "IssuedAt");
        PrintField("Expires At",    root, "ExpiresAt", "Never");
        PrintField("Max Ports",     root, "MaxPorts");
        PrintField("Features",      root, "Features", "ALL");

        // فحص انتهاء الصلاحية
        if (root.TryGetProperty("ExpiresAt", out var expEl)
            && expEl.ValueKind != JsonValueKind.Null
            && DateTime.TryParse(expEl.GetString(), out var exp))
        {
            bool expired = exp < DateTime.UtcNow;
            Console.WriteLine();
            if (expired)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"⚠️  EXPIRED on {exp:yyyy-MM-dd}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Valid — expires in {(int)(exp - DateTime.UtcNow).TotalDays} day(s)");
            }
            Console.ResetColor();
        }

        Console.WriteLine("══════════════════════════════════════════════");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to parse license: {ex.Message}");
        return 1;
    }
}

// ════════════════════════════════════════════════════════════
// Helper functions
// ════════════════════════════════════════════════════════════

static string Prompt(string label, string defaultVal)
{
    Console.Write($"  {label} ");
    if (!string.IsNullOrEmpty(defaultVal))
        Console.Write($"[{defaultVal}] ");
    string? input = Console.ReadLine()?.Trim();
    return string.IsNullOrEmpty(input) ? defaultVal : input;
}

static string PromptChoice(string label, string[] choices, string defaultVal)
{
    Console.Write($"  {label} ({string.Join("/", choices)}) [{defaultVal}]: ");
    string? input = Console.ReadLine()?.Trim();
    return choices.Contains(input, StringComparer.OrdinalIgnoreCase) ? input! : defaultVal;
}

static void PrintField(string label, JsonElement root, string key, string? fallback = null)
{
    string val = fallback ?? "(not set)";
    if (root.TryGetProperty(key, out var el) && el.ValueKind != JsonValueKind.Null)
    {
        val = el.ValueKind == JsonValueKind.Array
            ? string.Join(", ", el.EnumerateArray().Select(e => e.GetString() ?? ""))
            : el.ToString();
    }
    Console.WriteLine($"  {label,-18}: {val}");
}

static int DoHelp()    { ShowHelp(); return 0; }
static int DoUnknown(string cmd) { Console.Error.WriteLine($"Unknown command: {cmd}"); return 1; }

static void ShowHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  keygen               Generate new RSA 2048 key pair");
    Console.WriteLine("  issue  [private.pem] Create a new signed license (interactive)");
    Console.WriteLine("  verify <file> [pub]  Verify a license file signature");
    Console.WriteLine("  info   <file>        Show license details");
    Console.WriteLine("  hash   <password>    Generate PBKDF2 hash for AdminPasswordHash setting");
    Console.WriteLine();
    Console.WriteLine("Workflow:");
    Console.WriteLine("  1. admintool keygen");
    Console.WriteLine("  2. Copy public_key.pem into LicenseManager.PublicKeyPem");
    Console.WriteLine("  3. admintool issue  → creates license_<id>.key");
    Console.WriteLine("  4. Send license_<id>.key to customer as license.key");
    Console.WriteLine();
    Console.WriteLine("Admin Password Setup:");
    Console.WriteLine("  admintool hash MySecretPassword123");
    Console.WriteLine("  → Copy the output to appsettings.json → PrintServer.AdminApi.AdminPasswordHash");
}

// ════════════════════════════════════════════════════════════
// hash — توليد PBKDF2 hash لكلمة مرور Admin API
// ════════════════════════════════════════════════════════════
static int DoHashPassword(string[] args)
{
    string password = args.Length > 1
        ? args[1]
        : Prompt("Password to hash:", "");

    if (string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine("❌ Password cannot be empty.");
        return 1;
    }

    const int iterations = 310_000; // OWASP 2023 recommendation for PBKDF2-SHA256
    byte[] salt = new byte[16];
    System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
    using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(
        password, salt, iterations,
        System.Security.Cryptography.HashAlgorithmName.SHA256);
    byte[] hash = pbkdf2.GetBytes(32);
    string result = $"PBKDF2:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("✅ PBKDF2 Hash generated:");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine(result);
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("📋 Copy the above value to appsettings.json:");
    Console.WriteLine("   PrintServer → AdminApi → AdminPasswordHash");
    Console.ResetColor();
    return 0;
}

// ════════════════════════════════════════════════════════════
// RSA Sign / Verify (standalone, no shared DLL)
// ════════════════════════════════════════════════════════════

static string CreateSignedLicense(LicenseData data, string privateKeyPem)
{
    using var rsa = RSA.Create();
    rsa.ImportFromPem(privateKeyPem);

    var payload = new Dictionary<string, object?>
    {
        ["CustomerId"]   = data.CustomerId,
        ["CustomerName"] = data.CustomerName,
        ["LicenseType"]  = data.LicenseType,
        ["IssuedAt"]     = data.IssuedAt.ToString("o"),
        ["ExpiresAt"]    = data.ExpiresAt?.ToString("o"),
        ["MaxPorts"]     = data.MaxPorts,
        ["Features"]     = (object?)data.Features
    };

    string payloadJson = JsonSerializer.Serialize(payload,
        new JsonSerializerOptions { WriteIndented = false });
    byte[] signature   = rsa.SignData(Encoding.UTF8.GetBytes(payloadJson),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    payload["Signature"] = Convert.ToBase64String(signature);
    return JsonSerializer.Serialize(payload,
        new JsonSerializerOptions { WriteIndented = true });
}

static bool VerifyLicense(string licJson, string publicKeyPem)
{
    try
    {
        var doc  = JsonDocument.Parse(licJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("Signature", out var sigEl)) return false;
        byte[] sig = Convert.FromBase64String(sigEl.GetString() ?? "");

        var payloadDict = new Dictionary<string, JsonElement>();
        foreach (var prop in root.EnumerateObject())
            if (prop.Name != "Signature")
                payloadDict[prop.Name] = prop.Value;

        string payloadJson = JsonSerializer.Serialize(payloadDict,
            new JsonSerializerOptions { WriteIndented = false });

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return rsa.VerifyData(Encoding.UTF8.GetBytes(payloadJson),
            sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    catch { return false; }
}

// ════════════════════════════════════════════════════════════
// Data Models
// ════════════════════════════════════════════════════════════

internal class LicenseData
{
    public string   CustomerId   { get; set; } = "";
    public string   CustomerName { get; set; } = "";
    public string   LicenseType  { get; set; } = "Trial";
    public DateTime IssuedAt     { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt   { get; set; }
    public int      MaxPorts     { get; set; } = 1;
    public List<string>? Features { get; set; }
}
