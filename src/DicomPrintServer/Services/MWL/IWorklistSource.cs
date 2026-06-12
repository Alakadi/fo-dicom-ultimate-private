using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DicomPrintServer.Configuration;

namespace DicomPrintServer.Services.MWL
{
    /// <summary>
    /// واجهة مصدر بيانات قائمة العمل (MWL)
    /// كل مصدر (Database, FHIR, HL7, CSV) ينفذ هذه الواجهة
    /// </summary>
    public interface IWorklistSource
    {
        /// <summary>اسم المصدر للتعريف في اللوجز</summary>
        string SourceName { get; }

        /// <summary>
        /// يبحث عن عناصر قائمة العمل المطابقة لمعايير الاستعلام
        /// </summary>
        /// <param name="criteria">معايير البحث (PatientName, PatientID, DateRange, Modality, etc.)</param>
        /// <param name="maxResults">الحد الأقصى للنتائج</param>
        /// <param name="ct">CancellationToken</param>
        /// <returns>قائمة العناصر المطابقة</returns>
        Task<IReadOnlyList<WorklistItem>> FindAsync(
            MWLQueryCriteria criteria,
            int maxResults,
            CancellationToken ct = default);

        /// <summary>
        /// يضيف أو يحدث عنصر قائمة عمل
        /// </summary>
        Task<bool> UpsertAsync(WorklistItem item, CancellationToken ct = default);

        /// <summary>
        /// يحذف عنصر قائمة عمل
        /// </summary>
        Task<bool> DeleteAsync(string scheduledProcedureStepID, CancellationToken ct = default);

        /// <summary>
        /// يحصل على عنصر بواسطة ScheduledProcedureStepID
        /// </summary>
        Task<WorklistItem?> GetByIdAsync(string scheduledProcedureStepID, CancellationToken ct = default);

        /// <summary>تهيئة المصدر (فتح اتصال، تحميل كاش، إلخ)</summary>
        Task InitializeAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// معايير استعلام MWL - تتوافق مع مفاتيح DICOM C-FIND للقائمة
    /// </summary>
    public class MWLQueryCriteria
    {
        // Patient Identification
        public string? PatientName        { get; set; }
        public string? PatientID          { get; set; }
        public string? IssuerOfPatientID  { get; set; }
        public string? PatientBirthDate   { get; set; }
        public string? PatientSex         { get; set; }

        // Scheduled Procedure Step
        public string? ScheduledStationAET       { get; set; }
        public string? ScheduledProcedureStepStartDate { get; set; }
        public string? ScheduledProcedureStepEndDate   { get; set; }
        public string? Modality                     { get; set; }
        public string? ScheduledPerformingPhysicianName { get; set; }
        public string? ScheduledProcedureStepStatus   { get; set; }

        // Requested Procedure
        public string? RequestedProcedureID       { get; set; }
        public string? AccessionNumber            { get; set; }
        public string? StudyInstanceUID           { get; set; }

        // Date range helpers
        public string? StartDateFrom { get; set; } // yyyyMMdd
        public string? StartDateTo   { get; set; } // yyyyMMdd

        // Result limit
        public int MaxResults { get; set; } = 200;

        public bool IsEmpty =>
            string.IsNullOrEmpty(PatientName) &&
            string.IsNullOrEmpty(PatientID) &&
            string.IsNullOrEmpty(ScheduledStationAET) &&
            string.IsNullOrEmpty(ScheduledProcedureStepStartDate) &&
            string.IsNullOrEmpty(Modality) &&
            string.IsNullOrEmpty(AccessionNumber) &&
            string.IsNullOrEmpty(RequestedProcedureID) &&
            string.IsNullOrEmpty(StudyInstanceUID);
    }
}