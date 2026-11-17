using Android.Content;
using Android.Security.Keystore;
using DownloadManager.Mobile.Remote;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Inbound.App.Hosts;

/// <summary>
/// The paired servers' tokens (ANDROID.7), encrypted with AES-256-GCM under a key that lives in the Android Keystore and
/// never leaves it. Only the IV and the ciphertext are written, to the app's private preferences (the app has
/// allowBackup="false", so they are not backed up either). A token that can't be decrypted any more (the key was reset)
/// reads as null and the host asks to be paired again. Nothing here is ever logged.
/// </summary>
internal sealed class KeystoreSecretStore(Context context) : ISecretStore
{
    private const string KeyStoreName = "AndroidKeyStore";
    private const string Alias = "app.inbound.android.host-tokens";
    private const string Transformation = "AES/GCM/NoPadding";
    private const string Prefs = "inbound_host_secrets";
    private static readonly object Gate = new();

    private ISharedPreferences Store => context.GetSharedPreferences(Prefs, FileCreationMode.Private)!;

    public string? Read(string name)
    {
        lock (Gate)
        {
            var saved = Store.GetString(name, null);
            if (string.IsNullOrEmpty(saved)) return null;
            var parts = saved.Split(':');
            if (parts.Length != 2) return null;
            try
            {
                var key = ExistingKey();
                if (key is null) return null;
                var cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, Convert.FromBase64String(parts[0])));
                return System.Text.Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))!);
            }
            catch (Exception e) when (e is Java.Lang.Exception or FormatException)
            {
                // Not the secret (a reset key, a damaged entry): pairing again replaces it. The exception text is not logged.
                return null;
            }
        }
    }

    public void Write(string name, string secret)
    {
        lock (Gate)
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, ExistingKey() ?? NewKey());
            var sealedBytes = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(secret))!;
            var saved = Convert.ToBase64String(cipher.GetIV()!) + ":" + Convert.ToBase64String(sealedBytes);
            // Commit, not Apply: the pairing is reported as saved only once it is on disk.
            if (!Store.Edit()!.PutString(name, saved)!.Commit()) throw new IOException("The pairing could not be written.");
        }
    }

    public void Delete(string name)
    {
        lock (Gate) Store.Edit()!.Remove(name)!.Commit();
    }

    private static IKey? ExistingKey()
    {
        var store = KeyStore.GetInstance(KeyStoreName)!;
        store.Load(null);
        return store.GetKey(Alias, null);
    }

    private static IKey NewKey()
    {
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .Build());
        return generator.GenerateKey()!;
    }
}
