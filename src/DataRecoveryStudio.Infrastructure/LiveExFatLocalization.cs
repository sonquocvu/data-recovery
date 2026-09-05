namespace DataRecoveryStudio.Infrastructure;

public sealed partial class DictionaryLocalizationService
{
    static DictionaryLocalizationService()
    {
        var entries = new (string Key, string En, string Vi)[]
        {
            ("Results.LiveSubtitle", "Inspect filename, path and allocation evidence. Live results support metadata preview only.", "Kiểm tra bằng chứng tên, đường dẫn và cấp phát. Kết quả trực tiếp chỉ hỗ trợ xem siêu dữ liệu."),
            ("Capability.LiveExFatPreviewDisabled", "Live exFAT Standard Scan is a separate controlled preview and is disabled on this installation.", "Quét Tiêu chuẩn exFAT trực tiếp là bản xem trước có kiểm soát riêng và đang bị tắt."),
            ("Capability.LiveExFatAvailable", "Live read-only exFAT Standard Scan is available. Windows requests administrator approval only when you start.", "Có thể quét Tiêu chuẩn exFAT trực tiếp ở chế độ chỉ đọc. Windows chỉ yêu cầu quyền quản trị khi bắt đầu."),
            ("ScanMode.LiveExFatStandardBody", "Examines exFAT boot, FAT, bitmap, up-case and directory metadata. Source access is read-only; deleted-file content is not intentionally read.", "Kiểm tra siêu dữ liệu khởi động, FAT, bitmap, bảng chữ hoa và thư mục exFAT. Nguồn chỉ được đọc; không chủ động đọc nội dung tệp đã xóa."),
            ("ScanMode.LiveExFatBestEffort", "Live exFAT metadata is best effort. Matching sampled metadata does not establish a point-in-time snapshot.", "Siêu dữ liệu exFAT trực tiếp chỉ phản ánh thông tin thu thập được. Các mẫu siêu dữ liệu trùng khớp không tạo thành ảnh chụp tại một thời điểm."),
            ("Progress.ReadOnlyKind.ExFat", "Live exFAT · Standard Scan · Read-only", "exFAT trực tiếp · Quét Tiêu chuẩn · Chỉ đọc"),
            ("Results.LiveExFatAllocationNotice", "Live exFAT metadata is best effort, not a snapshot. Name confidence is separate from content recoverability. Allocation can change; metadata may overlap stale candidate ranges. Content preview and recovery are unavailable.", "Siêu dữ liệu exFAT trực tiếp không phải ảnh chụp nhất quán. Độ tin cậy tên tệp khác với khả năng khôi phục nội dung. Cấp phát có thể thay đổi; siêu dữ liệu có thể trùng vùng ứng viên cũ. Không hỗ trợ xem nội dung hay khôi phục."),
            ("Results.LiveExFatScanSummary", "{0} ({1}) · exFAT · Standard Scan · Live/read-only · {2} · {3} · {4:N0} directories · {5:N0} directory entries · {6:N0} FAT entries · {7:N0} candidates · {8}", "{0} ({1}) · exFAT · Quét Tiêu chuẩn · Trực tiếp/chỉ đọc · {2} · {3} · {4:N0} thư mục · {5:N0} mục thư mục · {6:N0} mục FAT · {7:N0} ứng viên · {8}"),
            ("Results.Created", "Created", "Ngày tạo"),
            ("Results.FileAttributes", "FILE ATTRIBUTES", "THUỘC TÍNH TỆP"),
            ("Results.Accessed", "Accessed", "Ngày truy cập"),
            ("Results.ExFat.ValidDataLength", "VALID-DATA LENGTH", "ĐỘ DÀI DỮ LIỆU HỢP LỆ"),
            ("Results.ExFat.Layout", "LAYOUT EVIDENCE", "BẰNG CHỨNG BỐ TRÍ"),
            ("Results.ExFat.Timestamps", "TIMESTAMP EVIDENCE", "BẰNG CHỨNG THỜI GIAN"),
            ("Results.ExFat.MetadataState", "METADATA STATE", "TRẠNG THÁI SIÊU DỮ LIỆU"),
            ("ExFatMetadata.Partial", "Partial metadata; allocation confidence reduced", "Siêu dữ liệu chưa đầy đủ; giảm độ tin cậy cấp phát"),
            ("ExFatMetadata.Damaged", "Damaged metadata", "Siêu dữ liệu hỏng"),
            ("ExFatMetadata.BestEffort", "Live metadata; best effort, no snapshot", "Siêu dữ liệu trực tiếp; không phải ảnh chụp nhất quán"),
            ("ExFatLayout.Contiguous", "Contiguous layout indicated", "Có dấu hiệu bố trí liên tục"),
            ("ExFatLayout.FatChain", "FAT chain layout indicated", "Có dấu hiệu bố trí theo chuỗi FAT"),
            ("ExFatTimestamp.Valid", "Valid timestamp and UTC offset", "Thời gian và độ lệch UTC hợp lệ"),
            ("ExFatTimestamp.OffsetUnknown", "UTC offset unknown", "Không rõ độ lệch UTC"),
            ("ExFatTimestamp.Invalid", "Invalid timestamp", "Thời gian không hợp lệ"),
            ("ExFatPath.CompleteActiveParent", "Path through active parent metadata", "Đường dẫn qua siêu dữ liệu thư mục cha đang hoạt động"),
            ("ExFatPath.NameUncertain", "Uncertain name in path", "Tên trong đường dẫn chưa chắc chắn"),
            ("ExFatPath.ParentDamaged", "Damaged parent metadata", "Siêu dữ liệu thư mục cha hỏng"),
            ("LiveScan.ExFat.Boot", "Reading exFAT boot metadata", "Đang đọc siêu dữ liệu khởi động exFAT"),
            ("LiveScan.ExFat.Directories", "Examining exFAT directories", "Đang kiểm tra thư mục exFAT"),
            ("LiveScan.ExFat.UpCase", "Validating exFAT up-case metadata", "Đang xác thực bảng chữ hoa exFAT"),
            ("LiveScan.ExFat.Ownership", "Checking active ownership", "Đang kiểm tra vùng đang được sử dụng"),
            ("LiveScan.ExFat.Allocation", "Assessing allocation evidence", "Đang đánh giá bằng chứng cấp phát"),
            ("LiveScan.ExFat.Finalizing", "Verifying sampled exFAT consistency evidence", "Đang xác minh các mẫu bằng chứng nhất quán exFAT"),
            ("ExFatName.VerifiedDeletedName", "Verified deleted-name metadata", "Siêu dữ liệu tên đã xóa được xác minh"),
            ("ExFatName.ChecksumRecoveredName", "Name supported by restored checksum", "Tên được hỗ trợ bởi tổng kiểm tra phục hồi"),
            ("ExFatName.HashVerifiedName", "Name hash verified", "Mã băm tên được xác minh"),
            ("ExFatName.ProbableName", "Probable name", "Tên có khả năng đúng"),
            ("ExFatName.AmbiguousName", "Ambiguous name", "Tên chưa rõ ràng"),
            ("ExFatName.DamagedName", "Damaged name", "Tên bị hỏng"),
            ("ExFatName.GeneratedFallbackRequired", "Fallback name required", "Cần dùng tên thay thế"),
            ("ExFatAllocation.ZeroLength", "Zero-length metadata", "Siêu dữ liệu tệp rỗng"),
            ("ExFatAllocation.ContiguousAllFree", "Contiguous range currently free", "Vùng liên tục hiện chưa cấp phát"),
            ("ExFatAllocation.ContiguousPartiallyAllocated", "Contiguous range partly allocated", "Vùng liên tục đã cấp phát một phần"),
            ("ExFatAllocation.ContiguousFullyAllocated", "Contiguous range allocated", "Vùng liên tục đã được cấp phát"),
            ("ExFatAllocation.PreservedChainAllFree", "Preserved chain currently free", "Chuỗi còn lưu hiện chưa cấp phát"),
            ("ExFatAllocation.PreservedChainPartiallyAllocated", "Preserved chain partly allocated", "Chuỗi còn lưu đã cấp phát một phần"),
            ("ExFatAllocation.PreservedChainFullyAllocated", "Preserved chain allocated", "Chuỗi còn lưu đã được cấp phát"),
            ("ExFatAllocation.ActiveOwnershipConflict", "Conflicts with active ownership", "Xung đột với vùng đang được sử dụng"),
            ("ExFatAllocation.BitmapUnavailable", "Allocation bitmap unavailable", "Không có bitmap cấp phát"),
            ("ExFatAllocation.FatChainMissing", "FAT chain missing", "Thiếu chuỗi FAT"),
            ("ExFatAllocation.FatChainCycle", "FAT chain cycle", "Chuỗi FAT có vòng lặp"),
            ("ExFatAllocation.FatChainDamaged", "FAT chain damaged", "Chuỗi FAT hỏng"),
            ("ExFatAllocation.FatBitmapDisagreement", "FAT and bitmap disagree", "FAT và bitmap không khớp"),
            ("ExFatAllocation.UnsupportedLayout", "Unsupported layout", "Bố trí không được hỗ trợ"),
            ("ExFatAllocation.DamagedMetadata", "Damaged metadata", "Siêu dữ liệu hỏng"),
            ("ExFatAllocation.Unknown", "Allocation unknown", "Không rõ trạng thái cấp phát"),
        };
        foreach (var (key, en, vi) in entries)
        {
            ((Dictionary<string, string>)English).Add(key, en);
            ((Dictionary<string, string>)Vietnamese).Add(key, vi);
        }
    }
}
