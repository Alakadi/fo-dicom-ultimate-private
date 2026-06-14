using System.Text;
using DicomPrintServer.Configuration;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Printing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// DICOM Print SCP handler — يُعالج اتصال واحد من جهاز طبي.
    /// يستخدم PrintConfigProvider لمعرفة إعدادات المنفذ بناءً على CalledAE.
    /// مبني على نموذج Print SCP الأصلي في samples/Core/Print SCP/PrintService.cs
    /// مع إضافة: دعم التكوين الديناميكي، JPG export، متعدد المنافذ.
    /// </summary>
    public class DicomPrintService : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
    {
        private static readonly DicomTransferSyntax[] _acceptedTransferSyntaxes =
        {
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian,
        };

        private readonly PrintConfigProvider  _configProvider;
        private readonly JpgExporter          _jpgExporter;
        private readonly PdfExporter          _pdfExporter;
        private readonly PrintMonitor         _monitor;
        private readonly WhatsAppNotifier?    _whatsApp;
        private readonly PdfSessionManager?   _pdfSessionMgr;
        private readonly IConnectionTracker   _connectionTracker;
        private readonly HisRisClient?        _hisRisClient;

        private FilmSession? _filmSession;
        private DicomPrinter? _printer;
        private ListenerConfig? _config;

        private PatientInfo? _currentPatientInfo;

        private readonly Dictionary<string, PrintJob> _printJobs = new();
        private bool _sendEventReports;
        private readonly object _lock = new();

        public string CallingAE { get; private set; } = string.Empty;
        public string CalledAE { get; private set; } = string.Empty;

        public DicomPrintService(
            INetworkStream stream,
            Encoding fallbackEncoding,
            ILogger log,
            DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
            _configProvider     = dependencies.ServiceProvider.GetRequiredService<PrintConfigProvider>();
            _jpgExporter        = dependencies.ServiceProvider.GetRequiredService<JpgExporter>();
            _pdfExporter        = dependencies.ServiceProvider.GetRequiredService<PdfExporter>();
            _monitor            = dependencies.ServiceProvider.GetRequiredService<PrintMonitor>();
            _whatsApp           = dependencies.ServiceProvider.GetService<WhatsAppNotifier>();
            _pdfSessionMgr      = dependencies.ServiceProvider.GetService<PdfSessionManager>();
            _connectionTracker  = dependencies.ServiceProvider.GetRequiredService<IConnectionTracker>();
            _hisRisClient       = dependencies.ServiceProvider.GetService<HisRisClient>();
        }

        #region IDicomServiceProvider

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            Logger.LogInformation(
                "Association request from AE: {Calling} → {Called} (IP: {IP})",
                association.CallingAE, association.CalledAE, association.RemoteHost);

            _config = _configProvider.GetConfig(association.CalledAE);

            if (_config == null)
            {
                Logger.LogError("Rejected: AET {Called} not configured", association.CalledAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            CallingAE = association.CallingAE;
            CalledAE = association.CalledAE;
            _printer = new DicomPrinter(_config.AET, _config.WindowsPrinterName);

            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification
                    || pc.AbstractSyntax == DicomUID.BasicGrayscalePrintManagementMeta
                    || pc.AbstractSyntax == DicomUID.BasicColorPrintManagementMeta
                    || pc.AbstractSyntax == DicomUID.Printer
                    || pc.AbstractSyntax == DicomUID.BasicFilmSession
                    || pc.AbstractSyntax == DicomUID.BasicFilmBox
                    || pc.AbstractSyntax == DicomUID.BasicGrayscaleImageBox
                    || pc.AbstractSyntax == DicomUID.BasicColorImageBox)
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                }
                else if (pc.AbstractSyntax == DicomUID.PrintJob)
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    _sendEventReports = true;
                }
                else
                {
                    Logger.LogWarning("Rejected abstract syntax {Syntax} from {AE}",
                        pc.AbstractSyntax, association.CallingAE);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            Logger.LogInformation("Association accepted from {CallingAE}", association.CallingAE);
            _connectionTracker.RegisterConnection(CallingAE, CalledAE, association.RemoteHost, _config.Port);
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            _connectionTracker.UnregisterConnection(CallingAE, CalledAE);
            Clean();
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            Logger.LogError("Abort received: source={Source}, reason={Reason}", source, reason);
            _connectionTracker.UnregisterConnection(CallingAE, CalledAE);
        }

        public void OnConnectionClosed(Exception? exception)
        {
            if (exception != null)
                Logger.LogError(exception, "Connection closed with error");
            _connectionTracker.UnregisterConnection(CallingAE, CalledAE);
            Clean();
        }

        #endregion

        #region C-ECHO

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            Logger.LogInformation("C-ECHO from {AE}", CallingAE);
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        #endregion

        #region N-CREATE

        public Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
        {
            lock (_lock)
            {
                if (request.SOPClassUID == DicomUID.BasicFilmSession)
                    return Task.FromResult(CreateFilmSession(request));

                if (request.SOPClassUID == DicomUID.BasicFilmBox)
                    return Task.FromResult(CreateFilmBox(request));

                return Task.FromResult(new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported));
            }
        }

        private DicomNCreateResponse CreateFilmSession(DicomNCreateRequest request)
        {
            if (_filmSession != null)
            {
                Logger.LogError("Film session already exists for {AE}", CallingAE);
                SendAbortAsync(DicomAbortSource.ServiceProvider, DicomAbortReason.NotSpecified).Wait();
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            var pc = request.PresentationContext;
            bool isColor = pc != null && pc.AbstractSyntax == DicomUID.BasicColorPrintManagementMeta;

            _filmSession = new FilmSession(request.SOPClassUID, request.SOPInstanceUID, request.Dataset, isColor);
            Logger.LogInformation("Film session created: {UID}", _filmSession.SOPInstanceUID.UID);

            // ── استعلام HIS/RIS لبيانات المريض ───────────────────────────────
            _currentPatientInfo = null;
            if (_hisRisClient != null)
            {
                string patientId = _filmSession.GetSingleValueOrDefault(DicomTag.PatientID, "");
                string patientName = _filmSession.GetSingleValueOrDefault(DicomTag.PatientName, "");
                
                if (!string.IsNullOrWhiteSpace(patientId) || !string.IsNullOrWhiteSpace(patientName))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _currentPatientInfo = await _hisRisClient!.GetPatientInfoAsync(patientId, patientName);
                            if (_currentPatientInfo != null)
                            {
                                Logger.LogInformation("HisRis: Found patient {Name} (Phone: {Phone})",
                                    _currentPatientInfo.PatientName, _currentPatientInfo.Phone ?? "none");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "HisRis query failed for patientId={Id}", patientId);
                        }
                    });
                }
            }

            if (string.IsNullOrEmpty(request.SOPInstanceUID?.UID))
                request.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, _filmSession.SOPInstanceUID);

            return new DicomNCreateResponse(request, DicomStatus.Success);
        }

        private DicomNCreateResponse CreateFilmBox(DicomNCreateRequest request)
        {
            if (_filmSession == null)
            {
                Logger.LogError("No film session for {AE}", CallingAE);
                SendAbortAsync(DicomAbortSource.ServiceProvider, DicomAbortReason.NotSpecified).Wait();
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            var filmBox = _filmSession.CreateFilmBox(request.SOPInstanceUID, request.Dataset);
            if (!filmBox.Initialize())
            {
                Logger.LogError("Failed to init film box {UID}", filmBox.SOPInstanceUID.UID);
                SendAbortAsync(DicomAbortSource.ServiceProvider, DicomAbortReason.NotSpecified).Wait();
                return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
            }

            Logger.LogInformation("Film box created: {UID}", filmBox.SOPInstanceUID.UID);
            if (string.IsNullOrEmpty(request.SOPInstanceUID?.UID))
                request.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, filmBox.SOPInstanceUID.UID);

            return new DicomNCreateResponse(request, DicomStatus.Success) { Dataset = filmBox };
        }

        #endregion

        #region N-SET

        public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
        {
            lock (_lock)
            {
                if (request.SOPClassUID == DicomUID.BasicFilmSession)
                    return Task.FromResult(SetFilmSession(request));

                if (request.SOPClassUID == DicomUID.BasicFilmBox)
                    return Task.FromResult(SetFilmBox(request));

                if (request.SOPClassUID == DicomUID.BasicColorImageBox
                    || request.SOPClassUID == DicomUID.BasicGrayscaleImageBox)
                    return Task.FromResult(SetImageBox(request));

                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported));
            }
        }

        private DicomNSetResponse SetImageBox(DicomNSetRequest request)
        {
            if (_filmSession == null)
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);

            var imageBox = _filmSession.FindImageBox(request.SOPInstanceUID);
            if (imageBox == null)
            {
                Logger.LogError("ImageBox {UID} not found", request.SOPInstanceUID.UID);
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            request.Dataset.CopyTo(imageBox);
            Logger.LogDebug("ImageBox {UID} updated", request.SOPInstanceUID.UID);
            return new DicomNSetResponse(request, DicomStatus.Success);
        }

        private DicomNSetResponse SetFilmBox(DicomNSetRequest request)
        {
            if (_filmSession == null)
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);

            var filmBox = _filmSession.FindFilmBox(request.SOPInstanceUID);
            if (filmBox == null)
            {
                Logger.LogError("FilmBox {UID} not found", request.SOPInstanceUID.UID);
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            request.Dataset.CopyTo(filmBox);
            filmBox.Initialize();

            var response = new DicomNSetResponse(request, DicomStatus.Success);
            response.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, filmBox.SOPInstanceUID);
            response.Dataset = filmBox;
            return response;
        }

        private DicomNSetResponse SetFilmSession(DicomNSetRequest request)
        {
            if (_filmSession == null || _filmSession.SOPInstanceUID.UID != request.SOPInstanceUID.UID)
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);

            request.Dataset.CopyTo(_filmSession);
            return new DicomNSetResponse(request, DicomStatus.Success);
        }

        #endregion

        #region N-DELETE

        public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
        {
            lock (_lock)
            {
                if (request.SOPClassUID == DicomUID.BasicFilmSession)
                    return Task.FromResult(DeleteFilmSession(request));

                if (request.SOPClassUID == DicomUID.BasicFilmBox)
                    return Task.FromResult(DeleteFilmBox(request));

                return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchSOPClass));
            }
        }

        private DicomNDeleteResponse DeleteFilmBox(DicomNDeleteRequest request)
        {
            if (_filmSession == null)
                return new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance);

            var status = _filmSession.DeleteFilmBox(request.SOPInstanceUID)
                ? DicomStatus.Success
                : DicomStatus.NoSuchObjectInstance;
            return new DicomNDeleteResponse(request, status);
        }

        private DicomNDeleteResponse DeleteFilmSession(DicomNDeleteRequest request)
        {
            if (_filmSession == null || !request.SOPInstanceUID.Equals(_filmSession.SOPInstanceUID))
                return new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance);

            if (_config?.SavePdf == true && _pdfSessionMgr != null)
            {
                var patientId = _filmSession.GetSingleValueOrDefault(DicomTag.PatientID, "");
                if (!string.IsNullOrEmpty(patientId))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _pdfSessionMgr.FlushSessionAsync(CalledAE, patientId);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to flush PDF session for {PatientId}", patientId);
                        }
                    });
                }
            }

            _filmSession = null;
            return new DicomNDeleteResponse(request, DicomStatus.Success);
        }

        #endregion

        #region N-GET

        public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
        {
            lock (_lock)
            {
                if (request.SOPClassUID == DicomUID.Printer
                    && request.SOPInstanceUID == DicomUID.PrinterInstance)
                    return Task.FromResult(GetPrinter(request));

                if (request.SOPClassUID == DicomUID.PrintJob)
                    return Task.FromResult(GetPrintJob(request));

                if (request.SOPClassUID == DicomUID.PrinterConfigurationRetrieval
                    && request.SOPInstanceUID == DicomUID.PrinterConfigurationRetrievalInstance)
                    return Task.FromResult(GetPrinterConfiguration(request));

                return Task.FromResult(new DicomNGetResponse(request, DicomStatus.NoSuchSOPClass));
            }
        }

        private DicomNGetResponse GetPrinter(DicomNGetRequest request)
        {
            var ds = new DicomDataset();

            if (request.Attributes?.Length > 0)
            {
                foreach (var tag in request.Attributes)
                    ds.Add(tag, _printer?.GetSingleValueOrDefault(tag, "") ?? "");
            }
            else
            {
                ds.Add(DicomTag.PrinterStatus, _printer?.PrinterStatus ?? "NORMAL");
                ds.Add(DicomTag.PrinterStatusInfo, _printer?.PrinterStatusInfo ?? "NORMAL");
                ds.Add(DicomTag.PrinterName, _printer?.PrinterName ?? CalledAE);
                ds.Add(DicomTag.Manufacturer, _printer?.Manufacturer ?? "DicomPrintServer");
                ds.Add(DicomTag.DateOfLastCalibration, DateTime.Today);
                ds.Add(DicomTag.TimeOfLastCalibration, DateTime.Now);
                ds.Add(DicomTag.ManufacturerModelName, _printer?.ManufacturerModelName ?? "v1.0");
                ds.Add(DicomTag.DeviceSerialNumber, "001");
                ds.Add(DicomTag.SoftwareVersions, "1.0");
            }

            return new DicomNGetResponse(request, DicomStatus.Success) { Dataset = ds };
        }

        private DicomNGetResponse GetPrinterConfiguration(DicomNGetRequest request)
        {
            var dataset = new DicomDataset();
            dataset.Add(new DicomSequence(DicomTag.PrinterConfigurationSequence, new DicomDataset()));
            var response = new DicomNGetResponse(request, DicomStatus.Success);
            response.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID);
            response.Dataset = dataset;
            return response;
        }

        private DicomNGetResponse GetPrintJob(DicomNGetRequest request)
        {
            if (!_printJobs.TryGetValue(request.SOPInstanceUID.UID, out var job))
                return new DicomNGetResponse(request, DicomStatus.NoSuchObjectInstance);

            var ds = new DicomDataset();
            if (request.Attributes?.Length > 0)
                foreach (var tag in request.Attributes)
                    ds.Add(tag, job.GetSingleValueOrDefault(tag, ""));

            return new DicomNGetResponse(request, DicomStatus.Success) { Dataset = ds };
        }

        #endregion

        #region N-ACTION (Trigger Print)

        public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
        {
            if (_filmSession == null)
            {
                Logger.LogError("No film session for N-ACTION from {AE}", CallingAE);
                return Task.FromResult(new DicomNActionResponse(request, DicomStatus.InvalidObjectInstance));
            }

            lock (_lock)
            {
                try
                {
                    var filmBoxList = new List<FilmBox>();

                    if (request.SOPClassUID == DicomUID.BasicFilmSession && request.ActionTypeID == 0x0001)
                    {
                        filmBoxList.AddRange(_filmSession.BasicFilmBoxes);
                        Logger.LogInformation("N-ACTION: print all film boxes in session {UID}",
                            _filmSession.SOPInstanceUID.UID);
                    }
                    else if (request.SOPClassUID == DicomUID.BasicFilmBox && request.ActionTypeID == 0x0001)
                    {
                        var filmBox = _filmSession.FindFilmBox(request.SOPInstanceUID);
                        if (filmBox == null)
                        {
                            Logger.LogError("FilmBox {UID} not found for N-ACTION", request.SOPInstanceUID.UID);
                            return Task.FromResult(new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance));
                        }
                        filmBoxList.Add(filmBox);
                    }
                    else
                    {
                        var status = request.ActionTypeID != 0x0001
                            ? DicomStatus.NoSuchActionType
                            : DicomStatus.NoSuchSOPClass;
                        return Task.FromResult(new DicomNActionResponse(request, status));
                    }

                    var printJob = new PrintJob(
                        null, _printer!, CallingAE, Logger, _config!,
                        _jpgExporter, _pdfExporter, _monitor, _whatsApp, _currentPatientInfo, _configProvider.ServerConfig.CenterName)
                    {
                        SendNEventReport = _sendEventReports
                    };

                    printJob.StatusUpdate += OnPrintJobStatusUpdate;
                    _printJobs[printJob.SOPInstanceUID.UID] = printJob;
                    printJob.Print(filmBoxList);

                    // M3-E: إضافة FilmBoxes لجلسة PDF المريض (إذا كان SavePdf مفعّلاً)
                    if (_config!.SavePdf && _pdfSessionMgr != null)
                    {
                        foreach (var fb in filmBoxList)
                            _pdfSessionMgr.AddFilmBox(fb, _config);
                    }

                    if (printJob.Error != null)
                        throw printJob.Error;

                    var result = new DicomDataset
                    {
                        new DicomSequence(DicomTag.ReferencedPrintJobSequenceRETIRED,
                            new DicomDataset(new DicomUniqueIdentifier(
                                DicomTag.ReferencedSOPClassUID, DicomUID.PrintJob)),
                            new DicomDataset(new DicomUniqueIdentifier(
                                DicomTag.ReferencedSOPInstanceUID, printJob.SOPInstanceUID)))
                    };

                    var response = new DicomNActionResponse(request, DicomStatus.Success);
                    response.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID);
                    response.Dataset = result;
                    return Task.FromResult(response);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "N-ACTION failed for {AE}", CallingAE);
                    return Task.FromResult(new DicomNActionResponse(request, DicomStatus.ProcessingFailure));
                }
            }
        }

        private void OnPrintJobStatusUpdate(object? sender, StatusUpdateEventArgs e)
        {
            if (sender is PrintJob job && job.SendNEventReport)
            {
                // Fire-and-forget with proper error handling to avoid ObjectDisposedException
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var report = new DicomNEventReportRequest(job.SOPClassUID, job.SOPInstanceUID, e.EventTypeId);
                        report.Dataset = new DicomDataset
                        {
                            { DicomTag.ExecutionStatusInfo, e.ExecutionStatusInfo },
                            { DicomTag.FilmSessionLabel, e.FilmSessionLabel },
                            { DicomTag.PrinterName, e.PrinterName }
                        };
                        await SendRequestAsync(report);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Service already disposed - this is expected during shutdown, ignore silently
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to send N-EVENT-REPORT for job {JobId}", job.SOPInstanceUID.UID);
                    }
                });
            }
        }

        #endregion

        #region N-EVENT-REPORT

        public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
            => Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));

        #endregion

        public void Clean()
        {
            lock (_lock)
            {
                _filmSession = null;
                _printJobs.Clear();
            }
        }
    }
}
