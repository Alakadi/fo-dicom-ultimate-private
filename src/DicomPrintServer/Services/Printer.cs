using FellowOakDicom;
using FellowOakDicom.Printing;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// يُمثّل حالة طابعة DICOM.
    /// نسخة موسّعة عن نموذج Print SCP الأصلي مع دعم التكوين.
    /// </summary>
    public class DicomPrinter : DicomDataset
    {
        public string PrinterAet { get; private set; }

        public string PrinterStatus
        {
            get => GetSingleValueOrDefault(DicomTag.PrinterStatus, "NORMAL");
            set => AddOrUpdate(DicomTag.PrinterStatus, value);
        }

        public string PrinterStatusInfo
        {
            get => GetSingleValueOrDefault(DicomTag.PrinterStatusInfo, "NORMAL");
            set => AddOrUpdate(DicomTag.PrinterStatusInfo, value);
        }

        public string PrinterName
        {
            get => GetSingleValueOrDefault(DicomTag.PrinterName, string.Empty);
            private set => Add(DicomTag.PrinterName, value);
        }

        public string Manufacturer
        {
            get => GetSingleValueOrDefault(DicomTag.Manufacturer, "DICOM Print Server");
            private set => Add(DicomTag.Manufacturer, value);
        }

        public string ManufacturerModelName
        {
            get => GetSingleValueOrDefault(DicomTag.ManufacturerModelName, "DicomPrintSrv v1.0");
            private set => Add(DicomTag.ManufacturerModelName, value);
        }

        public string DeviceSerialNumber
        {
            get => GetSingleValueOrDefault(DicomTag.DeviceSerialNumber, string.Empty);
            private set => Add(DicomTag.DeviceSerialNumber, value);
        }

        public string SoftwareVersions
        {
            get => GetSingleValueOrDefault(DicomTag.SoftwareVersions, "1.0");
            private set => Add(DicomTag.SoftwareVersions, value);
        }

        public DateTime DateTimeOfLastCalibration
        {
            get => this.GetDateTime(DicomTag.DateOfLastCalibration, DicomTag.TimeOfLastCalibration);
            private set
            {
                Add(DicomTag.DateOfLastCalibration, value);
                Add(DicomTag.TimeOfLastCalibration, value);
            }
        }

        public DicomPrinter(string aet, string windowsPrinterName = "")
        {
            PrinterAet = aet;
            DateTimeOfLastCalibration = DateTime.Now;
            PrinterStatus = "NORMAL";
            PrinterStatusInfo = "NORMAL";

            PrinterName = string.IsNullOrEmpty(windowsPrinterName)
                ? aet
                : windowsPrinterName;
        }
    }
}
