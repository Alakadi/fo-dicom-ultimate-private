namespace DicomPrintServer.Services
{
    /// <summary>
    /// معلومات المريض المسترجعة من HIS/RIS
    /// </summary>
    public class PatientInfo
    {
        public string  PatientId   { get; set; } = "";
        public string  PatientName { get; set; } = "";
        public string? Phone       { get; set; }
        public string? DateOfBirth { get; set; }
        public string? Email       { get; set; }
    }
}
