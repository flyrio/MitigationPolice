// 本文件提供“手动发送到小队频道”的能力；默认仅在用户点击时触发，并执行字符/长度安全检查。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace MitigationPolice.Chat;

public unsafe sealed class ChatSender : IDisposable {
    private const int MaxChatBytes = 500;

    private const string SendChatSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9";
    private const string SanitiseStringSignature = "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 70 4D 8B F8 4C 89 44 24 ?? 4C 8B 05 ?? ?? ?? ?? 44 8B E2";

    private readonly Func<bool> enabledProvider;

    private ProcessChatBoxDelegate? processChatBox;
    private SanitizeStringDelegate? sanitizeString;
    private string? lastInitError;

    public ChatSender(Func<bool> enabledProvider) {
        this.enabledProvider = enabledProvider;
        TryInitialize();
    }

    public bool CanSend => enabledProvider() && processChatBox != null;

    public string? LastError => lastInitError;

    public void Dispose() { }

    public bool TrySendPartyMessage(string message, out string? error) {
        error = null;
        if (!enabledProvider()) {
            error = "已在设置中禁用发送小队消息";
            return false;
        }

        if (processChatBox == null) {
            error = lastInitError ?? "聊天发送初始化失败";
            return false;
        }

        message = message.Trim();
        if (string.IsNullOrWhiteSpace(message)) {
            error = "消息为空";
            return false;
        }

        return TryExecuteCommand($"/p {message}", out error);
    }

    public bool TrySendPartyMessages(IEnumerable<string> messages, out string? error) {
        error = null;

        if (!enabledProvider()) {
            error = "已在设置中禁用发送小队消息";
            return false;
        }

        if (processChatBox == null) {
            error = lastInitError ?? "聊天发送初始化失败";
            return false;
        }

        var list = messages.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
        if (list.Count == 0) {
            error = "消息为空";
            return false;
        }

        for (var i = 0; i < list.Count; i++) {
            if (!TrySendPartyMessage(list[i], out var innerError)) {
                error = $"第 {i + 1} 行发送失败：{innerError ?? "未知错误"}";
                return false;
            }
        }

        return true;
    }

    private bool TryExecuteCommand(string command, out string? error) {
        error = null;
        if (!command.StartsWith("/", StringComparison.Ordinal)) {
            error = "命令必须以 / 开头";
            return false;
        }

        if (command.Contains('\n') || command.Contains('\r')) {
            error = "消息不能包含换行";
            return false;
        }

        if (sanitizeString != null) {
            var sanitized = SanitiseText(command);
            if (!string.Equals(sanitized, command, StringComparison.Ordinal)) {
                error = "消息包含非法字符";
                return false;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(command);
        if (bytes.Length == 0) {
            error = "消息为空";
            return false;
        }

        if (bytes.Length > MaxChatBytes) {
            error = $"消息过长（{bytes.Length} 字节）";
            return false;
        }

        SendMessageUnsafe(bytes);
        return true;
    }

    private void TryInitialize() {
        try {
            var addr = Service.SigScanner.ScanText(SendChatSignature);
            if (addr == IntPtr.Zero) {
                lastInitError = "无法定位聊天发送签名";
                return;
            }

            processChatBox = Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(addr);
        } catch (Exception ex) {
            lastInitError = $"聊天发送初始化失败: {ex.Message}";
            Service.PluginLog.Error(ex, "Failed to initialize chat sender");
            return;
        }

        try {
            var addr = Service.SigScanner.ScanText(SanitiseStringSignature);
            if (addr == IntPtr.Zero) {
                sanitizeString = null;
                return;
            }

            sanitizeString = Marshal.GetDelegateForFunctionPointer<SanitizeStringDelegate>(addr);
        } catch (Exception ex) {
            sanitizeString = null;
            Service.PluginLog.Warning(ex, "Failed to initialize chat sanitizer; will skip sanitization checks");
        }
    }

    private string SanitiseText(string text) {
        if (sanitizeString == null) {
            return text;
        }

        var uText = Utf8String.FromString(text);
        sanitizeString(uText, 0x27F, IntPtr.Zero);
        var result = uText->ToString();
        uText->Dtor();
        IMemorySpace.Free(uText);
        return result;
    }

    private void SendMessageUnsafe(byte[] message) {
        if (processChatBox == null) {
            throw new InvalidOperationException("Chat sender not initialized");
        }

        var uiModule = (IntPtr)Framework.Instance()->GetUIModule();

        using var payload = new ChatPayload(message);
        var mem1 = Marshal.AllocHGlobal(400);
        Marshal.StructureToPtr(payload, mem1, false);
        processChatBox(uiModule, mem1, IntPtr.Zero, 0);
        Marshal.FreeHGlobal(mem1);
    }

    private delegate void ProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unused, byte a4);

    private delegate void SanitizeStringDelegate(Utf8String* stringPtr, int a2, nint a3);

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct ChatPayload : IDisposable {
        [FieldOffset(0)]
        private readonly IntPtr textPtr;

        [FieldOffset(8)]
        private readonly ulong unk1;

        [FieldOffset(16)]
        private readonly ulong textLen;

        [FieldOffset(24)]
        private readonly ulong unk2;

        internal ChatPayload(byte[] stringBytes) {
            textPtr = Marshal.AllocHGlobal(stringBytes.Length + 30);
            Marshal.Copy(stringBytes, 0, textPtr, stringBytes.Length);
            Marshal.WriteByte(textPtr + stringBytes.Length, 0);
            textLen = (ulong)(stringBytes.Length + 1);
            unk1 = 64;
            unk2 = 0;
        }

        public void Dispose() {
            Marshal.FreeHGlobal(textPtr);
        }
    }
}
