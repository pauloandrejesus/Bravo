﻿namespace Sqlbi.Bravo.Services
{
    using CsvHelper;
    using CsvHelper.Configuration;
    using CsvHelper.TypeConversion;
    using LargeXlsx;
    using Microsoft.AnalysisServices.AdomdClient;
    using Sqlbi.Bravo.Infrastructure;
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Helpers;
    using Sqlbi.Bravo.Infrastructure.Services.ExportData;
    using Sqlbi.Bravo.Models;
    using System;
    using System.Data;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;

    public interface IExportDataService
    {
        ExportDataJob ExportDelimitedTextFile(PBIDesktopReport report, ExportDelimitedTextSettings settings, CancellationToken cancellationToken);

        ExportDataJob ExportDelimitedTextFile(PBICloudDataset dataset, ExportDelimitedTextSettings settings, CancellationToken cancellationToken);

        ExportDataJob ExportExcelFile(PBIDesktopReport report, ExportExcelSettings settings, CancellationToken cancellationToken);

        ExportDataJob ExportExcelFile(PBICloudDataset dataset, ExportExcelSettings settings, CancellationToken cancellationToken);

        ExportDataJob? QueryExportJob(PBIDesktopReport report);

        ExportDataJob? QueryExportJob(PBICloudDataset dataset);
    }

    internal class ExportDataService : IExportDataService
    {
        private static readonly TypeConverterOptions _defaultDelimitedTextTypeConverterOptions = new()
        {
            Formats = new[]
            {
                string.Format("yyyy-MM-dd HH:mm:ss{0}000", CultureInfo.CurrentCulture.NumberFormat.CurrencyDecimalSeparator)
            }
        };

        private readonly ExportDataJobMap<PBICloudDataset> _datasetJobs;
        private readonly ExportDataJobMap<PBIDesktopReport> _reportJobs;
        private readonly IPBICloudService _pbicloudService;

        public ExportDataService(IPBICloudService pbicloudService)
        {
            _pbicloudService = pbicloudService;

            _datasetJobs = new();
            _reportJobs = new();
        }

        public ExportDataJob ExportDelimitedTextFile(PBIDesktopReport report, ExportDelimitedTextSettings settings, CancellationToken cancellationToken)
        {
            var connectionString = ConnectionStringHelper.BuildForPBIDesktop(report.ServerName!);
            var job = _reportJobs.AddNew(report);

            try
            {
                ExportDelimitedTextFileImpl(job, settings, connectionString, report.DatabaseName, cancellationToken);
                job.SetCompleted();
            }
            catch (OperationCanceledException)
            {
                job.SetCanceled();
            }
            catch (IOException ex)
            {
                job.SetFailed();
                throw new BravoException(BravoProblem.ExportDataFileError, ex.Message, ex);
            }
            finally
            {
                _reportJobs.Remove(report);
            }

            return job;
        }

        public ExportDataJob ExportDelimitedTextFile(PBICloudDataset dataset, ExportDelimitedTextSettings settings, CancellationToken cancellationToken)
        {
            var (connectionString, databaseName) = dataset.GetConnectionParameters(_pbicloudService.CurrentAccessToken);
            var job = _datasetJobs.AddNew(dataset);

            try
            {
                ExportDelimitedTextFileImpl(job, settings, connectionString, databaseName, cancellationToken);
                job.SetCompleted();
            }
            catch (OperationCanceledException)
            {
                job.SetCanceled();
            }
            catch (IOException ex)
            {
                job.SetFailed();
                throw new BravoException(BravoProblem.ExportDataFileError, ex.Message, ex);
            }
            finally
            {
                _datasetJobs.Remove(dataset);
            }

            return job;
        }

        public ExportDataJob ExportExcelFile(PBIDesktopReport report, ExportExcelSettings settings, CancellationToken cancellationToken)
        {
            var connectionString = ConnectionStringHelper.BuildForPBIDesktop(report.ServerName!);
            var job = _reportJobs.AddNew(report);

            try
            {
                ExportExcelFileImpl(job, settings, connectionString, report.DatabaseName, cancellationToken);
                job.SetCompleted();
            }
            catch (OperationCanceledException)
            {
                job.SetCanceled();
            }
            catch (IOException ex)
            {
                job.SetFailed();
                throw new BravoException(BravoProblem.ExportDataFileError, ex.Message, ex);
            }
            finally
            {
                _reportJobs.Remove(report);
            }

            return job;
        }

        public ExportDataJob ExportExcelFile(PBICloudDataset dataset, ExportExcelSettings settings, CancellationToken cancellationToken)
        {
            var (connectionString, databaseName) = dataset.GetConnectionParameters(_pbicloudService.CurrentAccessToken);
            var job = _datasetJobs.AddNew(dataset);

            try
            {
                ExportExcelFileImpl(job, settings, connectionString, databaseName, cancellationToken);
                job.SetCompleted();
            }
            catch (OperationCanceledException)
            {
                job.SetCanceled();
            }
            catch (IOException ex)
            {
                job.SetFailed();
                throw new BravoException(BravoProblem.ExportDataFileError, ex.Message, ex);
            }
            finally
            {
                _datasetJobs.Remove(dataset);
            }

            return job;
        }

        public ExportDataJob? QueryExportJob(PBIDesktopReport report)
        {
            _reportJobs.TryGet(report, out var job);

            return job;
        }

        public ExportDataJob? QueryExportJob(PBICloudDataset dataset)
        {
            _datasetJobs.TryGet(dataset, out var job);

            return job;
        }

        private static void ExportDelimitedTextFileImpl(ExportDataJob job, ExportDelimitedTextSettings settings, string? connectionString, string? databaseName, CancellationToken cancellationToken)
        {
            _ = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _ = databaseName ?? throw new ArgumentNullException(nameof(databaseName));

            Directory.CreateDirectory(settings.ExportPath);

            var config = new CsvConfiguration(CultureInfo.CurrentCulture);
            {
                config.Delimiter = settings.Delimiter ?? CultureInfo.CurrentUICulture.TextInfo.ListSeparator;
                config.Validate();
            }

            using var connection = new AdomdConnection(connectionString);
            connection.Open();
            connection.ChangeDatabase(databaseName);

            using var command = connection.CreateCommand();
            cancellationToken.Register(() => command.Cancel());

            foreach (var tableName in settings.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var table = job.AddNew(tableName);
                {
                    // TODO: if the SSAS instance supports TOPNSKIP then use that to query batches of rows
                    command.CommandText = $"EVALUATE '{ tableName }'";

                    var fileTableName = tableName.ReplaceInvalidFileNameChars();
                    var fileName = Path.ChangeExtension(fileTableName, "csv");
                    var path = Path.Combine(settings.ExportPath, fileName);

                    Encoding encoding = settings.UnicodeEncoding
                        ? new UnicodeEncoding()
                        : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                    using var streamWriter = new StreamWriter(path, append: false, encoding);
                    using var csvWriter = new CsvWriter(streamWriter, config);
                    using var dataReader = command.ExecuteReader(CommandBehavior.SingleResult);
                    //using var dataReader = CreateTestDataReader();

                    WriteData(table, csvWriter, dataReader, shouldQuote: settings.QuoteStringFields, cancellationToken);
                }
                table.SetCompleted();
            }

            static void WriteData(ExportDataTable table, CsvWriter writer, IDataReader reader, bool shouldQuote, CancellationToken cancellationToken)
            {
                // output dates using ISO 8601 format
                writer.Context.TypeConverterOptionsCache.AddOptions(typeof(DateTime), options: _defaultDelimitedTextTypeConverterOptions);

                // write header
                for (int i = 0; i < reader.FieldCount; i++)
                    writer.WriteField(reader.GetName(i), shouldQuote);

                writer.NextRecord();

                // write data
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader[i];

                        if (reader.GetFieldType(i) == typeof(string))
                        {
                            writer.WriteField(reader.IsDBNull(i) ? string.Empty : value.ToString(), shouldQuote);
                        }
                        else
                        {
                            writer.WriteField(value);
                        }
                    }

                    table.Rows++;
                    writer.NextRecord();
                }
            }
        }

        private static void ExportExcelFileImpl(ExportDataJob job, ExportExcelSettings settings, string? connectionString, string? databaseName, CancellationToken cancellationToken)
        {
            _ = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _ = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
            
            var xlsxFile = new FileInfo(settings.ExportPath);
            Directory.CreateDirectory(path: xlsxFile.Directory?.FullName!);

            using var connection = new AdomdConnection(connectionString);
            connection.Open();
            connection.ChangeDatabase(databaseName);

            using var command = connection.CreateCommand();
            cancellationToken.Register(() => command.Cancel());

            using var fileStream = new FileStream(xlsxFile.FullName, FileMode.Create, FileAccess.Write);
            using var xlsxWriter = new XlsxWriter(fileStream);

            foreach (var tableName in settings.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var table = job.AddNew(tableName);
                {
                    // TODO: if the SSAS instance supports TOPNSKIP then use that to query batches of rows
                    command.CommandText = $"EVALUATE '{ tableName }'";

                    using var dataReader = command.ExecuteReader(CommandBehavior.SingleResult);
                    //using var dataReader = CreateTestDataReader();

                    xlsxWriter.BeginWorksheet(name: tableName, splitRow: 1);
                    {
                        WriteData(table, xlsxWriter, dataReader, cancellationToken);
                    }
                    xlsxWriter.SetAutoFilter(fromRow: 1, fromColumn: 1, xlsxWriter.CurrentRowNumber, dataReader.FieldCount);
                }
            }

            static void WriteData(ExportDataTable table, XlsxWriter writer, IDataReader reader, CancellationToken cancellationToken)
            {
                var headerStyle = new XlsxStyle(
                    font: new XlsxFont("Segoe UI", 9, Color.White, bold: true),
                    fill: new XlsxFill(Color.FromArgb(0, 0x45, 0x86)),
                    border: XlsxStyle.Default.Border,
                    numberFormat: XlsxStyle.Default.NumberFormat,
                    alignment: XlsxAlignment.Default);

                // write header
                writer.SetDefaultStyle(headerStyle).BeginRow();

                for (var i = 0; i < reader.FieldCount; i++)
                    writer.Write(reader.GetName(i));

                var rowCount = 1; // count header

                // write data
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    writer.SetDefaultStyle(XlsxStyle.Default).BeginRow();

                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader[i];

                        switch (value)
                        {
                            case null:
                                writer.Write();
                                break;
                            case int @int:
                                writer.Write(@int);
                                break;
                            case double @double:
                                writer.Write(@double);
                                break;
                            case decimal @decimal:
                                writer.Write(@decimal);
                                break;
                            case DateTime dateTime:
                                writer.Write(dateTime);
                                break;
                            case string @string:
                                writer.Write(@string);
                                break;
                            case bool @bool:
                                writer.Write(@bool.ToString());
                                break;
                            case long @long:
                                {
                                    if (@long > int.MinValue && @long < int.MaxValue)
                                        writer.Write(Convert.ToInt32(@long));
                                    else
                                        writer.Write(@long.ToString());
                                }
                                break;
                            default:
                                writer.Write(value.ToString());
                                break;
                        }
                    }

                    table.Rows++;

                    if (++rowCount >= 999_999)
                    {
                        // Break and exit if we have reached the limit of an xlsx file
                        table.SetTruncated();
                        return;
                    }
                }

                table.SetCompleted();
            }
        }

        private static IDataReader CreateTestData()
        {
            const int Columns = 100;
            const int Rows = 10_000;

            var table = new DataTable();

            for (int c = 0; c < Columns; c++)
            {
                table.Columns.Add(new DataColumn($"col{ c }", typeof(string)));
            }

            for (int r = 0; r < Rows; r++)
            {
                var row = table.NewRow();
                {
                    for (var c = 0; c < Columns; c++)
                    {
                        row[c] = (r == c) ? null : $"Sample text [{ c },{ r }]";
                    }
                }
                table.Rows.Add(row);
            }

            return table.CreateDataReader();
        }
    }
}
