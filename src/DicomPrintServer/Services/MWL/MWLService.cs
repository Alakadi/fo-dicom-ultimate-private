using System.Runtime.CompilerServices;
using System.Text;
using DicomPrintServer.Configuration;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services.MWL
{
    /// <summary>
    /// MWL SCP Service — يستقبل C-FIND من الأجهزة ويبحث في قائمة العمل.
    ///
    /// يتوافق مع DICOM PS3.4 Appendix A:
    ///   - SOP Class: 1.2.840.10008.5.1.4.1.1.20.4 (MWL Information Model - FIND)
    ///   - Presentation Context Accept: Explicit VR Little Endian
    ///   - يدعم wildcard search (*) لـ PatientName, ScheduledStationAET
    ///
    /// Flow:
    ///   1. Modality → C-FIND Request (MWL SOP Class)
    ///   2. MWLService → Query WorklistSource (DB/FHIR/HL7/CSV)
    ///   3. MWLService → C-FIND Response (Pending) لكل نتيجة
    ///   4. MWLService → C-FIND Response (Success) لإنهاء
    /// </summary>
    public class MWLService : DicomService, IDicomServiceProvider, IDicomCFindProvider
    {
        private static readonly DicomTransferSyntax[] _acceptedTransferSyntaxes =
        {
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian,
        };

        private readonly IWorklistSource _worklistSource;
        private readonly MWLConfig _config;
        private readonly MWLMonitor _monitor;
        private readonly ILogger<MWLService> _logger;

        public string CallingAE { get; private set; } = string.Empty;
        public string CalledAE { get; private set; } = string.Empty;

        public MWLService(
            INetworkStream stream,
            Encoding fallbackEncoding,
            ILogger log,
            DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
            _worklistSource = dependencies.ServiceProvider.GetRequiredService<IWorklistSource>();
            _config = dependencies.ServiceProvider.GetRequiredService<IOptions<PrintServerConfig>>().Value.MWL;
            _monitor = dependencies.ServiceProvider.GetRequiredService<MWLMonitor>();
            _logger = (ILogger<MWLService>)log;
        }

        #region IDicomServiceProvider

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            _logger.LogInformation(
                "MWL: Association request from {CallingAE} → {CalledAE} (IP: {RemoteHost})",
                association.CallingAE, association.CalledAE, association.RemoteHost);

            CallingAE = association.CallingAE;
            CalledAE = association.CalledAE;

            if (!string.IsNullOrEmpty(_config.ScheduledAET) &&
                _config.RequireScheduledAET &&
                CallingAE != _config.ScheduledAET)
            {
                _logger.LogWarning(
                    "MWL: Rejected {CallingAE} — not in ScheduledAET whitelist ({Allowed})",
                    CallingAE, _config.ScheduledAET);
                _monitor.RecordAssociationRejected(CallingAE, CalledAE, "AET not in whitelist");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind)
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    _logger.LogDebug("MWL: Accepted C-FIND presentation context from {CallingAE}", CallingAE);
                }
                else if (pc.AbstractSyntax == DicomUID.Verification)
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    _logger.LogDebug("MWL: Accepted C-ECHO presentation context from {CallingAE}", CallingAE);
                }
                else
                {
                    _logger.LogWarning(
                        "MWL: Rejected abstract syntax {Syntax} from {CallingAE}",
                        pc.AbstractSyntax, CallingAE);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            _monitor.RecordAssociationAccepted(CallingAE, CalledAE, association.RemoteHost);
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            _logger.LogDebug("MWL: Association release from {CallingAE}", CallingAE);
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            _logger.LogWarning("MWL: Abort from {CallingAE}: source={Source}, reason={Reason}",
                CallingAE, source, reason);
        }

        public void OnConnectionClosed(Exception? exception)
        {
            if (exception != null)
                _logger.LogWarning(exception, "MWL: Connection closed with error for {CallingAE}", CallingAE);
        }

        #endregion

        #region IDicomCFindProvider

        public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
        {
            if (request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind)
            {
                _logger.LogWarning("MWL: Non-MWL SOP class received: {SOP}", request.SOPClassUID.UID);
                yield return new DicomCFindResponse(request, DicomStatus.SOPClassNotSupported);
                yield break;
            }

            var criteria = BuildQueryCriteria(request.Dataset);
            int maxResults = _config.MaxResults;

            _logger.LogInformation(
                "MWL: C-FIND from {CallingAE} — PatientName={PN}, PatientID={PID}, Station={Station}, Date={Date}, Modality={Mod}",
                CallingAE,
                criteria.PatientName ?? "*",
                criteria.PatientID ?? "*",
                criteria.ScheduledStationAET ?? "*",
                criteria.ScheduledProcedureStepStartDate ?? "*",
                criteria.Modality ?? "*");

            IReadOnlyList<WorklistItem>? items = null;
            Exception? queryError = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                items = await _worklistSource.FindAsync(criteria, maxResults);
            }
            catch (Exception ex)
            {
                queryError = ex;
            }

            sw.Stop();

            if (queryError != null)
            {
                _logger.LogError(queryError, "MWL: C-FIND query failed for {CallingAE}", CallingAE);
                _monitor.RecordQueryError(CallingAE, queryError.Message);
                yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
                yield break;
            }

            _monitor.RecordQuery(
                CallingAE,
                criteria.PatientName ?? "*",
                criteria.PatientID ?? "*",
                items!.Count,
                sw.Elapsed);

            _logger.LogInformation(
                "MWL: Found {Count} worklist item(s) for {CallingAE}",
                items.Count, CallingAE);

            foreach (var item in items!)
            {
                var ds = BuildResponseDataset(item);
                yield return new DicomCFindResponse(request, DicomStatus.Pending)
                {
                    Dataset = ds
                };
            }

            yield return new DicomCFindResponse(request, DicomStatus.Success);
        }

        #endregion

        #region Query Building

        private MWLQueryCriteria BuildQueryCriteria(DicomDataset dataset)
        {
            var criteria = new MWLQueryCriteria
            {
                MaxResults = _config.MaxResults
            };

            if (dataset.TryGetSingleValue(DicomTag.PatientName, out string? patientName))
                criteria.PatientName = patientName;

            if (dataset.TryGetSingleValue(DicomTag.PatientID, out string? patientId))
                criteria.PatientID = patientId;

            if (dataset.TryGetSingleValue(DicomTag.IssuerOfPatientID, out string? issuerId))
                criteria.IssuerOfPatientID = issuerId;

            if (dataset.TryGetSingleValue(DicomTag.PatientBirthDate, out string? dob))
                criteria.PatientBirthDate = dob;

            if (dataset.TryGetSingleValue(DicomTag.PatientSex, out string? sex))
                criteria.PatientSex = sex;

            if (dataset.TryGetSingleValue(DicomTag.ScheduledStationAETitle, out string? stationAet))
                criteria.ScheduledStationAET = stationAet;

            if (dataset.TryGetSingleValue(DicomTag.ScheduledProcedureStepStartDate, out string? startDate))
                criteria.ScheduledProcedureStepStartDate = startDate;

            if (dataset.TryGetSingleValue(DicomTag.ScheduledProcedureStepEndDate, out string? endDate))
                criteria.ScheduledProcedureStepEndDate = endDate;

            if (dataset.TryGetSingleValue(DicomTag.Modality, out string? modality))
                criteria.Modality = modality;

            if (dataset.TryGetSingleValue(DicomTag.ScheduledPerformingPhysicianName, out string? physician))
                criteria.ScheduledPerformingPhysicianName = physician;

            if (dataset.TryGetSingleValue(DicomTag.AccessionNumber, out string? accession))
                criteria.AccessionNumber = accession;

            if (dataset.TryGetSingleValue(DicomTag.RequestedProcedureID, out string? reqProcId))
                criteria.RequestedProcedureID = reqProcId;

            if (dataset.TryGetSingleValue(DicomTag.StudyInstanceUID, out string? studyUid))
                criteria.StudyInstanceUID = studyUid;

            if (dataset.TryGetSingleValue(DicomTag.ScheduledProcedureStepStatus, out string? stepStatus))
                criteria.ScheduledProcedureStepStatus = stepStatus;

            return criteria;
        }

        private DicomDataset BuildResponseDataset(WorklistItem item)
        {
            var ds = new DicomDataset();

            // Scheduled Procedure Step Module
            if (!string.IsNullOrEmpty(item.ScheduledStationAET))
                ds.Add(DicomTag.ScheduledStationAETitle, item.ScheduledStationAET);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepStartDate))
                ds.Add(DicomTag.ScheduledProcedureStepStartDate, item.ScheduledProcedureStepStartDate);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepStartTime))
                ds.Add(DicomTag.ScheduledProcedureStepStartTime, item.ScheduledProcedureStepStartTime);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepEndDate))
                ds.Add(DicomTag.ScheduledProcedureStepEndDate, item.ScheduledProcedureStepEndDate);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepEndTime))
                ds.Add(DicomTag.ScheduledProcedureStepEndTime, item.ScheduledProcedureStepEndTime);

            if (!string.IsNullOrEmpty(item.ScheduledPerformingPhysicianName))
                ds.Add(DicomTag.ScheduledPerformingPhysicianName, item.ScheduledPerformingPhysicianName);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepDescription))
                ds.Add(DicomTag.ScheduledProcedureStepDescription, item.ScheduledProcedureStepDescription);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepID))
                ds.Add(DicomTag.ScheduledProcedureStepID, item.ScheduledProcedureStepID);

            if (!string.IsNullOrEmpty(item.ScheduledStationName))
                ds.Add(DicomTag.ScheduledStationName, item.ScheduledStationName);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepLocation))
                ds.Add(DicomTag.ScheduledProcedureStepLocation, item.ScheduledProcedureStepLocation);

            if (!string.IsNullOrEmpty(item.ScheduledProcedureStepStatus))
                ds.Add(DicomTag.ScheduledProcedureStepStatus, item.ScheduledProcedureStepStatus);

            if (!string.IsNullOrEmpty(item.RequestedProcedurePriority))
                ds.Add(DicomTag.RequestedProcedurePriority, item.RequestedProcedurePriority);

            if (!string.IsNullOrEmpty(item.PatientTransportArrangements))
                ds.Add(DicomTag.PatientTransportArrangements, item.PatientTransportArrangements);

            // Requested Procedure Module
            if (!string.IsNullOrEmpty(item.RequestedProcedureID))
                ds.Add(DicomTag.RequestedProcedureID, item.RequestedProcedureID);

            if (!string.IsNullOrEmpty(item.RequestedProcedureDescription))
                ds.Add(DicomTag.RequestedProcedureDescription, item.RequestedProcedureDescription);

            if (!string.IsNullOrEmpty(item.AccessionNumber))
                ds.Add(DicomTag.AccessionNumber, item.AccessionNumber);

            if (!string.IsNullOrEmpty(item.ReferringPhysicianName))
                ds.Add(DicomTag.ReferringPhysicianName, item.ReferringPhysicianName);

            if (!string.IsNullOrEmpty(item.RequestingPhysician))
                ds.Add(DicomTag.RequestingPhysician, item.RequestingPhysician);

            if (!string.IsNullOrEmpty(item.RequestingService))
                ds.Add(DicomTag.RequestingService, item.RequestingService);

            if (!string.IsNullOrEmpty(item.ImagingServiceRequestComments))
                ds.Add(DicomTag.ImagingServiceRequestComments, item.ImagingServiceRequestComments);

            if (!string.IsNullOrEmpty(item.StudyInstanceUID))
                ds.Add(DicomTag.StudyInstanceUID, item.StudyInstanceUID);

            // Patient Identification Module
            if (!string.IsNullOrEmpty(item.PatientName))
                ds.Add(DicomTag.PatientName, item.PatientName);

            if (!string.IsNullOrEmpty(item.PatientID))
                ds.Add(DicomTag.PatientID, item.PatientID);

            if (!string.IsNullOrEmpty(item.IssuerOfPatientID))
                ds.Add(DicomTag.IssuerOfPatientID, item.IssuerOfPatientID);

            if (!string.IsNullOrEmpty(item.OtherPatientIDs))
                ds.Add(DicomTag.OtherPatientIDsRETIRED, item.OtherPatientIDs);

            if (!string.IsNullOrEmpty(item.PatientBirthDate))
                ds.Add(DicomTag.PatientBirthDate, item.PatientBirthDate);

            if (!string.IsNullOrEmpty(item.PatientSex))
                ds.Add(DicomTag.PatientSex, item.PatientSex);

            if (!string.IsNullOrEmpty(item.PatientWeight))
                ds.Add(DicomTag.PatientWeight, item.PatientWeight);

            if (!string.IsNullOrEmpty(item.ConfidentialityConstraint))
                ds.Add(DicomTag.ConfidentialityConstraintOnPatientDataDescription, item.ConfidentialityConstraint);

            if (!string.IsNullOrEmpty(item.PatientAddress))
                ds.Add(DicomTag.PatientAddress, item.PatientAddress);

            if (!string.IsNullOrEmpty(item.PatientTelephoneNumbers))
                ds.Add(DicomTag.PatientTelephoneNumbers, item.PatientTelephoneNumbers);

            if (!string.IsNullOrEmpty(item.PatientComments))
                ds.Add(DicomTag.PatientComments, item.PatientComments);

            return ds;
        }

        #endregion
    }
}