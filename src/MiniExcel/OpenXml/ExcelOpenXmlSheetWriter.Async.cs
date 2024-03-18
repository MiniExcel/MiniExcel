﻿using MiniExcelLibs.Utils;
using MiniExcelLibs.Zip;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static MiniExcelLibs.Utils.ImageHelper;

namespace MiniExcelLibs.OpenXml
{
    internal partial class ExcelOpenXmlSheetWriter : IExcelWriter
    {
        public async Task SaveAsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            GenerateDefaultOpenXml();

            switch (_value)
            {
                case IDictionary<string, object> sheets:
                {
                    var sheetId = 0;
                    _sheets.RemoveAt(0);//TODO:remove
                    foreach (var sheet in sheets)
                    {
                        sheetId++;
                        var sheetInfos = GetSheetInfos(sheet.Key);
                        var sheetDto = sheetInfos.ToDto(sheetId);
                        _sheets.Add(sheetDto); //TODO:remove

                        currentSheetIndex = sheetId;

                        await CreateSheetXmlAsync(sheet.Value, sheetDto.Path, cancellationToken);
                    }

                    break;
                }

                case DataSet sheets:
                {
                    var sheetId = 0;
                    _sheets.RemoveAt(0);//TODO:remove
                    foreach (DataTable dt in sheets.Tables)
                    {
                        sheetId++;
                        var sheetInfos = GetSheetInfos(dt.TableName);
                        var sheetDto = sheetInfos.ToDto(sheetId);
                        _sheets.Add(sheetDto); //TODO:remove

                        currentSheetIndex = sheetId;

                        await CreateSheetXmlAsync(dt, sheetDto.Path, cancellationToken);
                    }

                    break;
                }

                default:
                    //Single sheet export.
                    currentSheetIndex++;

                    await CreateSheetXmlAsync(_value, _sheets[0].Path, cancellationToken);
                    break;
            }

            GenerateEndXml();
            _archive.Dispose();
        }

        private async Task CreateSheetXmlAsync(object value, string sheetPath, CancellationToken cancellationToken)
        {
            ZipArchiveEntry entry = _archive.CreateEntry(sheetPath, CompressionLevel.Fastest);
            using (var zipStream = entry.Open())
            using (MiniExcelAsyncStreamWriter writer = new MiniExcelAsyncStreamWriter(zipStream, _utf8WithBom, _configuration.BufferSize, cancellationToken))
            {
                if (value == null)
                {
                    await WriteEmptySheetAsync(writer);
                    goto End; //for re-using code
                }

                //DapperRow

                switch (value)
                {
                    case IDataReader dataReader:
                        await GenerateSheetByIDataReaderAsync(writer, dataReader);
                        break;
                    case IEnumerable enumerable:
                        await GenerateSheetByEnumerableAsync(writer, enumerable);
                        break;
                    case DataTable dataTable:
                        await GenerateSheetByDataTableAsync(writer, dataTable);
                        break;
                    default:
                        throw new NotImplementedException($"Type {value.GetType().FullName} is not implemented. Please open an issue.");
                }
            }
        End: //for re-using code
            _zipDictionary.Add(sheetPath, new ZipPackageInfo(entry, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        }

        private async Task WriteEmptySheetAsync(MiniExcelAsyncStreamWriter writer)
        {
            await writer.WriteAsync($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main""><x:dimension ref=""A1""/><x:sheetData></x:sheetData></x:worksheet>");
        }

        private async Task GenerateSheetByIDataReaderAsync(MiniExcelAsyncStreamWriter writer, IDataReader reader)
        {
            long dimensionWritePosition = 0;
            await writer.WriteAsync($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");
            var xIndex = 1;
            var yIndex = 1;
            var maxColumnIndex = 0;
            var maxRowIndex = 0;
            {

                if (_configuration.FastMode)
                {
                    dimensionWritePosition = await writer.WriteAndFlushAsync($@"<x:dimension ref=""");
                    await writer.WriteAsync("                              />"); // end of code will be replaced
                }

                var props = new List<ExcelColumnInfo>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    var prop = GetColumnInfosFromDynamicConfiguration(columnName);
                    props.Add(prop);
                }
                maxColumnIndex = props.Count;

                await WriteColumnsWidthsAsync(writer, props);

                await writer.WriteAsync("<x:sheetData>");
                int fieldCount = reader.FieldCount;
                if (_printHeader)
                {
                    await PrintHeaderAsync(writer, props);
                    yIndex++;
                }

                while (reader.Read())
                {
                    await writer.WriteAsync($"<x:row r=\"{yIndex}\">");
                    xIndex = 1;
                    for (int i = 0; i < fieldCount; i++)
                    {
                        var cellValue = reader.GetValue(i);
                        await WriteCellAsync(writer, yIndex, xIndex, cellValue, null);
                        xIndex++;
                    }
                    await writer.WriteAsync($"</x:row>");
                    yIndex++;
                }

                // Subtract 1 because cell indexing starts with 1
                maxRowIndex = yIndex - 1;
            }

            await writer.WriteAsync("</x:sheetData>");
            if (_configuration.AutoFilter)
                await writer.WriteAsync($"<x:autoFilter ref=\"{GetDimensionRef(maxRowIndex, maxColumnIndex)}\" />");
            await writer.WriteAndFlushAsync("</x:worksheet>");

            if (_configuration.FastMode)
            {
                writer.SetPosition(dimensionWritePosition);
                await writer.WriteAndFlushAsync($@"{GetDimensionRef(maxRowIndex, maxColumnIndex)}""");
            }
        }

        private async Task GenerateSheetByEnumerableAsync(MiniExcelAsyncStreamWriter writer, IEnumerable values)
        {
            var maxColumnIndex = 0;
            var maxRowIndex = 0;
            List<ExcelColumnInfo> props = null;
            string mode = null;

            int? rowCount = null;
            var collection = values as ICollection;
            if (collection != null)
            {
                rowCount = collection.Count;
            }
            else if (!_configuration.FastMode)
            {
                // The row count is only required up front when not in fastmode
                collection = new List<object>(values.Cast<object>());
                rowCount = collection.Count;
            }

            // Get the enumerator once to ensure deferred linq execution
            var enumerator = (collection ?? values).GetEnumerator();

            // Move to the first item in order to inspect the value type and determine whether it is empty
            var empty = !enumerator.MoveNext();

            if (empty)
            {
                // only when empty IEnumerable need to check this issue #133  https://github.com/shps951023/MiniExcel/issues/133
                var genericType = TypeHelper.GetGenericIEnumerables(values).FirstOrDefault();
                if (genericType == null || genericType == typeof(object) // sometime generic type will be object, e.g: https://user-images.githubusercontent.com/12729184/132812859-52984314-44d1-4ee8-9487-2d1da159f1f0.png
                    || typeof(IDictionary<string, object>).IsAssignableFrom(genericType)
                    || typeof(IDictionary).IsAssignableFrom(genericType))
                {
                    await WriteEmptySheetAsync(writer);
                    return;
                }
                else
                {
                    SetGenericTypePropertiesMode(genericType, ref mode, out maxColumnIndex, out props);
                }
            }
            else
            {
                var firstItem = enumerator.Current;
                if (firstItem is IDictionary<string, object> genericDic)
                {
                    mode = "IDictionary<string, object>";
                    props = GetDictionaryColumnInfo(genericDic, null);
                    maxColumnIndex = props.Count;
                }
                else if (firstItem is IDictionary dic)
                {
                    mode = "IDictionary";
                    props = GetDictionaryColumnInfo(null, dic);
                    //maxColumnIndex = dic.Keys.Count; 
                    maxColumnIndex = props.Count; // why not using keys, because ignore attribute ![image](https://user-images.githubusercontent.com/12729184/163686902-286abb70-877b-4e84-bd3b-001ad339a84a.png)
                }
                else
                {
                    SetGenericTypePropertiesMode(firstItem.GetType(), ref mode, out maxColumnIndex, out props);
                }
            }

            await writer.WriteAsync($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" >");

            long dimensionWritePosition = 0;

            // We can write the dimensions directly if the row count is known
            if (_configuration.FastMode && rowCount == null)
            {
                // Write a placeholder for the table dimensions and save thee position for later
                dimensionWritePosition = await writer.WriteAndFlushAsync("<x:dimension ref=\"");
                await writer.WriteAsync("                              />");
            }
            else
            {
                maxRowIndex = rowCount.Value + (_printHeader && rowCount > 0 ? 1 : 0);
                await writer.WriteAsync($@"<x:dimension ref=""{GetDimensionRef(maxRowIndex, maxColumnIndex)}""/>");
            }

            //cols:width
            await WriteColumnsWidthsAsync(writer, props);

            //header
            await writer.WriteAsync($@"<x:sheetData>");
            var yIndex = 1;
            var xIndex = 1;
            if (_printHeader)
            {
                await PrintHeaderAsync(writer, props);
                yIndex++;
            }

            if (!empty)
            {
                // body
                switch (mode) //Dapper Row
                {
                    case "IDictionary<string, object>":
                        maxRowIndex = await GenerateSheetByColumnInfoAsync<IDictionary<string, object>>(writer, enumerator, props, xIndex, yIndex);
                        break;
                    case "IDictionary":
                        maxRowIndex = await GenerateSheetByColumnInfoAsync<IDictionary>(writer, enumerator, props, xIndex, yIndex);
                        break;
                    case "Properties":
                        maxRowIndex = await GenerateSheetByColumnInfoAsync<object>(writer, enumerator, props, xIndex, yIndex);
                        break;
                    default:
                        throw new NotImplementedException($"Type {values.GetType().FullName} is not implemented. Please open an issue.");
                }
            }

            await writer.WriteAsync("</x:sheetData>");
            if (_configuration.AutoFilter)
                await writer.WriteAsync($"<x:autoFilter ref=\"{GetDimensionRef(maxRowIndex, maxColumnIndex)}\" />");

            // The dimension has already been written if row count is defined
            if (_configuration.FastMode && rowCount == null)
            {
                // Flush and save position so that we can get back again.
                var pos = await writer.FlushAsync();

                // Seek back and write the dimensions of the table
                writer.SetPosition(dimensionWritePosition);
                await writer.WriteAndFlushAsync($@"{GetDimensionRef(maxRowIndex, maxColumnIndex)}""");
                writer.SetPosition(pos);
            }

            await writer.WriteAsync("<x:drawing  r:id=\"drawing" + currentSheetIndex + "\" /></x:worksheet>");
        }

        private async Task GenerateSheetByDataTableAsync(MiniExcelAsyncStreamWriter writer, DataTable value)
        {
            var xy = ExcelOpenXmlUtils.ConvertCellToXY("A1");

            //GOTO Top Write:
            await writer.WriteAsync($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");
            {
                var yIndex = xy.Item2;

                // dimension
                var maxRowIndex = value.Rows.Count + (_printHeader && value.Rows.Count > 0 ? 1 : 0);
                var maxColumnIndex = value.Columns.Count;
                await writer.WriteAsync($@"<x:dimension ref=""{GetDimensionRef(maxRowIndex, maxColumnIndex)}""/>");

                var props = new List<ExcelColumnInfo>();
                for (var i = 0; i < value.Columns.Count; i++)
                {
                    var columnName = value.Columns[i].Caption ?? value.Columns[i].ColumnName;
                    var prop = GetColumnInfosFromDynamicConfiguration(columnName);
                    props.Add(prop);
                }

                await WriteColumnsWidthsAsync(writer, props);

                await writer.WriteAsync("<x:sheetData>");
                if (_printHeader)
                {
                    await writer.WriteAsync($"<x:row r=\"{yIndex}\">");
                    var xIndex = xy.Item1;
                    foreach (var p in props)
                    {
                        var r = ExcelOpenXmlUtils.ConvertXyToCell(xIndex, yIndex);
                        await WriteCAsync(writer, r, columnName: p.ExcelColumnName);
                        xIndex++;
                    }

                    await writer.WriteAsync($"</x:row>");
                    yIndex++;
                }

                for (int i = 0; i < value.Rows.Count; i++)
                {
                    await writer.WriteAsync($"<x:row r=\"{yIndex}\">");
                    var xIndex = xy.Item1;

                    for (int j = 0; j < value.Columns.Count; j++)
                    {
                        var cellValue = value.Rows[i][j];
                        await WriteCellAsync(writer, yIndex, xIndex, cellValue, null);
                        xIndex++;
                    }
                    await writer.WriteAsync($"</x:row>");
                    yIndex++;
                }
            }
            await writer.WriteAsync("</x:sheetData></x:worksheet>");
        }

        private static async Task WriteColumnsWidthsAsync(MiniExcelAsyncStreamWriter writer, IEnumerable<ExcelColumnInfo> props)
        {
            var ecwProps = props.Where(x => x?.ExcelColumnWidth != null).ToList();
            if (ecwProps.Count <= 0)
                return;
            await writer.WriteAsync($@"<x:cols>");
            foreach (var p in ecwProps)
            {
                await writer.WriteAsync(
                    $@"<x:col min=""{p.ExcelColumnIndex + 1}"" max=""{p.ExcelColumnIndex + 1}"" width=""{p.ExcelColumnWidth}"" customWidth=""1"" />");
            }

            await writer.WriteAsync($@"</x:cols>");
        }

        private static async Task PrintHeaderAsync(MiniExcelAsyncStreamWriter writer, List<ExcelColumnInfo> props)
        {
            var xIndex = 1;
            var yIndex = 1;
            await writer.WriteAsync($"<x:row r=\"{yIndex}\">");

            foreach (var p in props)
            {
                if (p == null)
                {
                    xIndex++; //reason : https://github.com/shps951023/MiniExcel/issues/142
                    continue;
                }

                var r = ExcelOpenXmlUtils.ConvertXyToCell(xIndex, yIndex);
                WriteCAsync(writer, r, columnName: p.ExcelColumnName);
                xIndex++;
            }

            await writer.WriteAsync("</x:row>");
        }

        private static async Task WriteCAsync(MiniExcelAsyncStreamWriter writer, string r, string columnName)
        {
            await writer.WriteAsync($"<x:c r=\"{r}\" t=\"str\" s=\"1\">");
            await writer.WriteAsync($"<x:v>{ExcelOpenXmlUtils.EncodeXML(columnName)}"); //issue I45TF5
            await writer.WriteAsync($"</x:v>");
            await writer.WriteAsync($"</x:c>");
        }

        private async Task WriteCellAsync(MiniExcelAsyncStreamWriter writer, int rowIndex, int cellIndex, object value, ExcelColumnInfo p)
        {
            var columname = ExcelOpenXmlUtils.ConvertXyToCell(cellIndex, rowIndex);
            var s = "2";
            var valueIsNull = value is null || value is DBNull;

            if (_configuration.EnableWriteNullValueCell && valueIsNull)
            {
                await writer.WriteAsync($"<x:c r=\"{columname}\" s=\"{s}\"></x:c>");
                return;
            }

            var tuple = GetCellValue(rowIndex, cellIndex, value, p, valueIsNull);

            s = tuple.Item1;
            var t = tuple.Item2;
            var v = tuple.Item3;

            if (v != null && (v.StartsWith(" ", StringComparison.Ordinal) || v.EndsWith(" ", StringComparison.Ordinal))) /*Prefix and suffix blank space will lost after SaveAs #294*/
                await writer.WriteAsync($"<x:c r=\"{columname}\" {(t == null ? "" : $"t =\"{t}\"")} s=\"{s}\" xml:space=\"preserve\"><x:v>{v}</x:v></x:c>");
            else
                //t check avoid format error ![image](https://user-images.githubusercontent.com/12729184/118770190-9eee3480-b8b3-11eb-9f5a-87a439f5e320.png)
                await writer.WriteAsync($"<x:c r=\"{columname}\" {(t == null ? "" : $"t =\"{t}\"")} s=\"{s}\"><x:v>{v}</x:v></x:c>");
        }

        private async Task<int> GenerateSheetByColumnInfoAsync<T>(MiniExcelAsyncStreamWriter writer, IEnumerator value, List<ExcelColumnInfo> props, int xIndex = 1, int yIndex = 1)
        {
            var isDic = typeof(T) == typeof(IDictionary);
            var isDapperRow = typeof(T) == typeof(IDictionary<string, object>);
            do
            {
                // The enumerator has already moved to the first item
                T v = (T)value.Current;

                await writer.WriteAsync($"<x:row r=\"{yIndex}\">");
                var cellIndex = xIndex;
                foreach (var p in props)
                {
                    if (p == null) //reason:https://github.com/shps951023/MiniExcel/issues/142
                    {
                        cellIndex++;
                        continue;
                    }
                    object cellValue = null;
                    if (isDic)
                    {
                        cellValue = ((IDictionary)v)[p.Key];
                        //WriteCell(writer, yIndex, cellIndex, cellValue, null); // why null because dictionary that needs to check type every time
                        //TODO: user can specefic type to optimize efficiency
                    }
                    else if (isDapperRow)
                    {
                        cellValue = ((IDictionary<string, object>)v)[p.Key.ToString()];
                    }
                    else
                    {
                        cellValue = p.Property.GetValue(v);
                    }
                    await WriteCellAsync(writer, yIndex, cellIndex, cellValue, p);


                    cellIndex++;
                }
                await writer.WriteAsync($"</x:row>");
                yIndex++;
            } while (value.MoveNext());

            return yIndex - 1;
        }
    }
}
