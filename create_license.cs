using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var privateKeyPem = File.ReadAllText("private_key.pem");

var payload = new Dictionary<string, object?>
{
    ["CustomerId"] = "TRIAL-8H",
    ["CustomerName"] = "عميل تجريبي 8 ساعات",
    ["LicenseType"] = "Trial",
    ["IssuedAt"] = DateTime.UtcNow.ToString("o"),
    ["ExpiresAt"] = DateTime.UtcNow.AddHours(8).ToString("o"),
    ["MaxPorts"] = 1,
    ["Features"] = new[] { "JPG", "PDF", "MultiPort" }
};

string payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

using var rsa = RSA.Create();
rsa.ImportFromPem(privateKeyPem);
byte[] signature = rsa.SignData(Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

payload["Signature"] = Convert.ToBase64String(signature);

string signedJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText("license_trial_8h.key", signedJson, Encoding.UTF8);
Console.WriteLine("License created: license_trial_8h.key");