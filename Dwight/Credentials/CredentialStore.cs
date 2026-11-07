using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Dwight.Credentials
{
  /// <summary>
  /// Stores the user's API key in the Windows Credential Manager via the Win32 API
  /// (ADR-011: keys live in the OS credential store, never plaintext; ADR-026: Windows-first,
  /// so a direct P/Invoke beats pulling in a cross-platform secrets dependency for v1).
  /// The key is never written to logs or dump files.
  /// </summary>
  internal static class CredentialStore
  {
    /// <summary>One credential target per cloud provider (ADR-066). Ollama-local needs none.</summary>
    public const string AnthropicTarget = "Dwight:Anthropic";
    public const string OpenAITarget = "Dwight:OpenAI";
    public const string GoogleTarget = "Dwight:Google";
    public const string OllamaCloudTarget = "Dwight:OllamaCloud";

    /// <summary>The credential target for a provider id (WP-1.9), or null for a provider that needs no key
    /// (Ollama-local). The provider ids match <c>DwightConfig.Provider*</c>.</summary>
    public static string TargetFor(string provider)
    {
      switch (provider)
      {
        case "Anthropic": return AnthropicTarget;
        case "OpenAI": return OpenAITarget;
        case "Google": return GoogleTarget;
        case "OllamaCloud": return OllamaCloudTarget;
        default: return null;   // OllamaLocal (or unknown) needs no stored key
      }
    }

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static bool Exists(string target) => Load(target) != null;

    /// <summary>Write (or overwrite) the secret for <paramref name="target"/>.</summary>
    public static void Save(string target, string secret)
    {
      if (string.IsNullOrEmpty(secret))
        throw new ArgumentException("Refusing to store an empty API key.", nameof(secret));

      var blob = Encoding.Unicode.GetBytes(secret);
      var blobPtr = Marshal.AllocHGlobal(blob.Length);
      try
      {
        Marshal.Copy(blob, 0, blobPtr, blob.Length);

        var cred = new CREDENTIAL
        {
          Type = CRED_TYPE_GENERIC,
          TargetName = target,
          CredentialBlobSize = (uint)blob.Length,
          CredentialBlob = blobPtr,
          Persist = CRED_PERSIST_LOCAL_MACHINE,
          UserName = "Dwight",
        };

        if (!CredWrite(ref cred, 0))
          throw new InvalidOperationException(
            $"Could not store the API key (Win32 error {Marshal.GetLastWin32Error()}).");
      }
      finally
      {
        Marshal.FreeHGlobal(blobPtr);
      }
    }

    /// <summary>Read the secret, or null if none is stored.</summary>
    public static string Load(string target)
    {
      if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
        return null;

      try
      {
        var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
        if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
          return null;

        var bytes = new byte[cred.CredentialBlobSize];
        Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
        return Encoding.Unicode.GetString(bytes);
      }
      finally
      {
        CredFree(handle);
      }
    }

    /// <summary>Remove the stored secret (no-op if absent).</summary>
    public static void Delete(string target)
    {
      CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
      public uint Flags;
      public uint Type;
      public string TargetName;
      public string Comment;
      public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
      public uint CredentialBlobSize;
      public IntPtr CredentialBlob;
      public uint Persist;
      public uint AttributeCount;
      public IntPtr Attributes;
      public string TargetAlias;
      public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
  }
}
