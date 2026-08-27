using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class DictionaryLocalizationService : ILocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["App.Title"] = "Data Recovery Studio",
        ["App.MockBadge"] = "PHASE 1 · MOCK DATA",
        ["Nav.Devices"] = "Devices",
        ["Nav.Results"] = "Recovery results",
        ["Nav.Settings"] = "Settings",
        ["Device.Title"] = "Choose a device to scan",
        ["Device.Subtitle"] = "Select a volume below. Phase 1 uses realistic mock devices and never accesses your disks.",
        ["ScanMode.Title"] = "How should we search?",
        ["ScanMode.Subtitle"] = "Choose a scan mode for the selected mock device. Results are estimates, never guarantees.",
        ["ScanMode.Standard"] = "Standard Scan",
        ["ScanMode.StandardBody"] = "Fast metadata search that aims to preserve names, dates, and folder structure.",
        ["ScanMode.Deep"] = "Deep Scan",
        ["ScanMode.DeepBody"] = "Thorough signature search. Slower, and original names or folders may be unavailable.",
        ["Action.StartScan"] = "Start scan",
        ["Action.Back"] = "Back",
        ["Action.Cancel"] = "Cancel safely",
        ["Action.Pause"] = "Pause · coming later",
        ["Progress.Title"] = "Scanning your device",
        ["Progress.MockEstimate"] = "Mock progress · no physical device is being read",
        ["Results.Title"] = "Recovery results",
        ["Results.Subtitle"] = "Review mock estimates, preview details, and choose files to recover.",
        ["Results.Search"] = "Search file names or folders",
        ["Results.Recover"] = "Choose recovery destination",
        ["Results.Preview"] = "Preview & details",
        ["Results.SelectPrompt"] = "Select a result to inspect it.",
        ["Results.Filters"] = "File type",
        ["Settings.Title"] = "Settings",
        ["Settings.Appearance"] = "Appearance",
        ["Settings.Language"] = "Language",
        ["Safety.Notice"] = "Recover to a different physical device. Recovery can never be guaranteed.",
        ["Status.Excellent"] = "Excellent · mock estimate",
        ["Status.Good"] = "Good · mock estimate",
        ["Status.Poor"] = "Poor · mock estimate",
        ["Status.Unknown"] = "Unknown · mock estimate",
    };

    private static readonly IReadOnlyDictionary<string, string> Vietnamese = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["App.Title"] = "Data Recovery Studio",
        ["App.MockBadge"] = "GIAI ĐOẠN 1 · DỮ LIỆU MÔ PHỎNG",
        ["Nav.Devices"] = "Thiết bị",
        ["Nav.Results"] = "Kết quả khôi phục",
        ["Nav.Settings"] = "Cài đặt",
        ["Device.Title"] = "Chọn thiết bị để quét",
        ["Device.Subtitle"] = "Chọn một ổ đĩa bên dưới. Giai đoạn 1 chỉ dùng thiết bị mô phỏng và không truy cập ổ đĩa thật.",
        ["ScanMode.Title"] = "Bạn muốn tìm kiếm thế nào?",
        ["ScanMode.Subtitle"] = "Chọn chế độ quét cho thiết bị mô phỏng. Kết quả chỉ là ước tính, không phải bảo đảm.",
        ["ScanMode.Standard"] = "Quét tiêu chuẩn",
        ["ScanMode.StandardBody"] = "Tìm nhanh trong siêu dữ liệu, ưu tiên giữ tên, ngày và cấu trúc thư mục.",
        ["ScanMode.Deep"] = "Quét sâu",
        ["ScanMode.DeepBody"] = "Tìm kỹ theo chữ ký tệp. Chậm hơn và có thể không còn tên hay thư mục gốc.",
        ["Action.StartScan"] = "Bắt đầu quét",
        ["Action.Back"] = "Quay lại",
        ["Action.Cancel"] = "Hủy an toàn",
        ["Action.Pause"] = "Tạm dừng · sắp có",
        ["Progress.Title"] = "Đang quét thiết bị",
        ["Progress.MockEstimate"] = "Tiến trình mô phỏng · không đọc thiết bị thật",
        ["Results.Title"] = "Kết quả khôi phục",
        ["Results.Subtitle"] = "Xem ước tính mô phỏng, chi tiết và chọn tệp cần khôi phục.",
        ["Results.Search"] = "Tìm tên tệp hoặc thư mục",
        ["Results.Recover"] = "Chọn nơi khôi phục",
        ["Results.Preview"] = "Xem trước & chi tiết",
        ["Results.SelectPrompt"] = "Chọn một kết quả để xem chi tiết.",
        ["Results.Filters"] = "Loại tệp",
        ["Settings.Title"] = "Cài đặt",
        ["Settings.Appearance"] = "Giao diện",
        ["Settings.Language"] = "Ngôn ngữ",
        ["Safety.Notice"] = "Hãy khôi phục sang thiết bị vật lý khác. Không thể bảo đảm khôi phục thành công.",
        ["Status.Excellent"] = "Rất tốt · ước tính mô phỏng",
        ["Status.Good"] = "Tốt · ước tính mô phỏng",
        ["Status.Poor"] = "Kém · ước tính mô phỏng",
        ["Status.Unknown"] = "Chưa rõ · ước tính mô phỏng",
    };

    private string _languageCode = "en-US";

    public event EventHandler? LanguageChanged;

    public string LanguageCode => _languageCode;

    public string this[string key]
    {
        get
        {
            var selected = _languageCode == "vi-VN" ? Vietnamese : English;
            return selected.TryGetValue(key, out var value)
                ? value
                : English.TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode == "vi-VN" ? "vi-VN" : "en-US";
        if (string.Equals(_languageCode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _languageCode = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
