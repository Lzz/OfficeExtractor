﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Security.Policy;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentServices.Modules.Extractors.OfficeExtractor.CompoundFileStorage;
using DocumentServices.Modules.Extractors.OfficeExtractor.Exceptions;
using DocumentServices.Modules.Extractors.OfficeExtractor.Helpers;

namespace DocumentServices.Modules.Extractors.OfficeExtractor
{
    /// <summary>
    /// This class is used to extract embedded files from Word, Excel and PowerPoint files
    /// </summary>
    public class Extractor
    {
        #region CheckFileNameAndOutputFolder
        /// <summary>
        /// Checks if the <see cref="inputFile"/> and <see cref="outputFolder"/> is valid
        /// </summary>
        /// <param name="inputFile"></param>
        /// <param name="outputFolder"></param>
        /// <exception cref="ArgumentNullException">Raised when the <see cref="inputFile"/> or <see cref="outputFolder"/> is null or empty</exception>
        /// <exception cref="FileNotFoundException">Raised when the <see cref="inputFile"/> does not exists</exception>
        /// <exception cref="DirectoryNotFoundException">Raised when the <see cref="outputFolder"/> does not exists</exception>
        private static void CheckFileNameAndOutputFolder(string inputFile, string outputFolder)
        {
            if (string.IsNullOrEmpty(inputFile))
                throw new ArgumentNullException(inputFile);

            if (string.IsNullOrEmpty(outputFolder))
                throw new ArgumentNullException(outputFolder);

            if (!File.Exists(inputFile))
                throw new FileNotFoundException(inputFile);

            if (!Directory.Exists(outputFolder))
                throw new DirectoryNotFoundException(outputFolder);
        }
        #endregion

        #region ExtractToFolder
        /// <summary>
        /// Extracts all the embedded object from the Microsoft Office <see cref="inputFile"/> to the 
        /// <see cref="outputFolder"/> and returns the files with full path as a list of strings
        /// </summary>
        /// <param name="inputFile">The Microsoft Office file</param>
        /// <param name="outputFolder">The output folder</param>
        /// <returns>List with files or en empty list when there are nog embedded files</returns>
        /// <exception cref="ArgumentNullException">Raised when the <see cref="inputFile"/> or <see cref="outputFolder"/> is null or empty</exception>
        /// <exception cref="FileNotFoundException">Raised when the <see cref="inputFile"/> does not exists</exception>
        /// <exception cref="DirectoryNotFoundException">Raised when the <see cref="outputFolder"/> does not exists</exception>
        /// <exception cref="OEFileTypeNotSupported">Raised when the Microsoft Office File Type is not supported</exception>
        /// <exception cref="OEFileIsPasswordProtected">Raised when the <see cref="inputFile"/> is password protected</exception>
        public List<string> ExtractToFolder(string inputFile, string outputFolder)
        {
            CheckFileNameAndOutputFolder(inputFile, outputFolder);
            
            var extension = Path.GetExtension(inputFile);
            if (extension != null)
                extension = extension.ToUpperInvariant();

            outputFolder = FileManager.CheckForBackSlash(outputFolder);

            switch (extension)
            {
                case ".DOC":
                case ".DOT":
                    // Word 97 - 2003
                    return ExtractFromWordBinaryFormat(inputFile, outputFolder);

                case ".DOCM":
                case ".DOCX":
                case ".DOTM":
                    // Word 2007 - 2013
                    return ExtractFromOfficeOpenXmlFormat(inputFile, "/word/embeddings/", outputFolder);

                case ".XLS":
                case ".XLT":
                case ".XLW":
                    // Excel 97 - 2003
                    return ExtractFromExcelBinaryFormat(inputFile, outputFolder, "MBD");

                case ".XLSB":
                case ".XLSM":
                case ".XLSX":
                case ".XLTM":
                case ".XLTX":
                    // Excel 2007 - 2013
                    return ExtractFromOfficeOpenXmlFormat(inputFile, "/excel/embeddings/", outputFolder);

                case ".POT":
                case ".PPT":
                case ".PPS":
                    // PowerPoint 97 - 2003
                    return ExtractFromPowerPointBinaryFormat(inputFile, outputFolder);

                case ".POTM":
                case ".POTX":
                case ".PPSM":
                case ".PPSX":
                case ".PPTM":
                case ".PPTX":
                    // PowerPoint 2007 - 2013
                    return ExtractFromOfficeOpenXmlFormat(inputFile, "/ppt/embeddings/", outputFolder);

                default:
                    throw new OEFileTypeNotSupported("The file '" + Path.GetFileName(inputFile) +
                                                     "' is not supported, only .DOC, .DOCM, .DOCX, .DOT, .DOTM, .XLS, .XLSB, .XLSM, .XLSX, .XLT, " +
                                                     ".XLTM, .XLTX, .XLW, .POT, .PPT, .POTM, .POTX, .PPS, .PPSM, .PPSX, .PPTM and .PPTX are supported");
            }
        }
        #endregion

        #region ExtractFromWordBinaryFormat
        /// <summary>
        /// This method saves all the Word embedded binary objects from the <see cref="inputFile"/> to the
        /// <see cref="outputFolder"/>
        /// </summary>
        /// <param name="inputFile">The binary Word file</param>
        /// <param name="outputFolder">The output folder</param>
        /// <returns></returns>
        /// <exception cref="OEFileIsPasswordProtected">Raised when the <see cref="inputFile"/> is password protected</exception>
        private List<string> ExtractFromWordBinaryFormat(string inputFile, string outputFolder)
        {
            var compoundFile = new CompoundFile(inputFile);
            
            var result = new List<string>();

            if (!compoundFile.RootStorage.ExistsStorage("ObjectPool")) return result;
            var objectPoolStorage = compoundFile.RootStorage.GetStorage("ObjectPool") as CFStorage;
            if (objectPoolStorage == null) return result;
            // Multiple objects are stored as children of the storage object
            foreach (var child in objectPoolStorage.Children)
            {
                var childStorage = child as CFStorage;
                if (childStorage == null) continue;
                var extractedFileName = ExtractFromStorageNode(childStorage, outputFolder);
                if (extractedFileName != null)
                    result.Add(extractedFileName);
            }

            return result;
        }
        #endregion

        #region ExcelBinaryFormatIsPasswordProtected
        /// <summary>
        /// Returns true when the Excel file is password protected
        /// </summary>
        /// <param name="compoundFile"></param>
        /// <returns></returns>
        /// <exception cref="OEFileIsCorrupt">Raised when the file is corrupt</exception>
        public static bool ExcelBinaryFormatIsPasswordProtected(CompoundFile compoundFile)
        {
            if (!compoundFile.RootStorage.ExistsStream("WorkBook")) return false;
            var stream = compoundFile.RootStorage.GetStream("WorkBook") as CFStream;
            if (stream == null) return false;

            var bytes = stream.GetData();
            using (var memoryStream = new MemoryStream(bytes))
            using (var binaryReader = new BinaryReader(memoryStream))
            {
                // Get the record type, at the beginning of the stream this should always be the BOF
                var recordType = binaryReader.ReadUInt16();

                // Something seems to be wrong, we would expect a BOF but for some reason it isn't so stop it
                if (recordType != 0x809) 
                    throw new OEFileIsCorrupt("The file '" + Path.GetFileName(compoundFile.FileName) + "' is corrupt");

                var recordLength = binaryReader.ReadUInt16();
                binaryReader.BaseStream.Position += recordLength;

                // Search after the BOF for the FilePass record, this starts with 2F hex
                recordType = binaryReader.ReadUInt16();
                return (recordType == 0x2F);
            }
        }
        #endregion

        #region ExcelBinaryFormatSetWorkbookVisibility
        /// <summary>
        /// When a Excel document is embedded in for example a Word document the Workbook
        /// is set to hidden. Don't know why Microsoft does this but they do. To solve this
        /// problem we seek the WINDOW1 record in the BOF record of the stream. In there a
        /// gbit structure is located. The first bit in this structure controls the visibility
        /// of the workbook, so we check if this bit is set to 1 (hidden) en is so set it to 0.
        /// Normally a Workbook stream only contains one WINDOW record but when it is embedded
        /// it will contain 2 or more records.
        /// </summary>
        /// <param name="compoundFile"></param>
        public static void ExcelBinaryFormatSetWorkbookVisibility(ref CompoundFile compoundFile)
        {
            if (!compoundFile.RootStorage.ExistsStream("WorkBook")) return;
            var stream = compoundFile.RootStorage.GetStream("WorkBook") as CFStream;
            if (stream == null) return;

            var bytes = stream.GetData();

            using (var memoryStream = new MemoryStream(bytes))
            using (var binaryReader = new BinaryReader(memoryStream))
            {
                // Get the record type, at the beginning of the stream this should always be the BOF
                var recordType = binaryReader.ReadUInt16();
                var recordLength = binaryReader.ReadUInt16();

                // Something seems to be wrong, we would expect a BOF but for some reason it isn't 
                if (recordType != 0x809)
                    throw new OEFileIsCorrupt("The file is corrupt");

                binaryReader.BaseStream.Position += recordLength;

                while (binaryReader.BaseStream.Position < binaryReader.BaseStream.Length)
                {
                    recordType = binaryReader.ReadUInt16();
                    recordLength = binaryReader.ReadUInt16();
                    
                    // Window1 record (0x3D)
                    if (recordType == 0x3D)
                    {
                        // ReSharper disable UnusedVariable
                        var xWn = binaryReader.ReadUInt16();
                        var yWn = binaryReader.ReadUInt16();
                        var dxWn = binaryReader.ReadUInt16();
                        var dyWn = binaryReader.ReadUInt16();
                        // ReSharper restore UnusedVariable

                        // The grbit contains the bit that hides the sheet
                        var grbit = binaryReader.ReadBytes(2);
                        var bitArray = new BitArray(grbit);

                        // When the bit is set then unset it (bitArray.Get(0) == true)
                        if (bitArray.Get(0))
                        {
                            bitArray.Set(0, false);

                            // Copy the byte back into the stream, 2 positions back so that we overwrite the old bytes
                            bitArray.CopyTo(bytes, (int) binaryReader.BaseStream.Position - 2);
                        }

                        break;
                    }
                    binaryReader.BaseStream.Position += recordLength;
                }
            }

            stream.SetData(bytes);
        }
        #endregion

        #region ExcelBinaryFormatSetWorkbookVisibility
        /// <summary>
        /// This method set the workbook in an Open XML Format Excel file to visible
        /// </summary>
        /// <param name="stream"></param>
        public static void ExcelOpenXmlFormatSetWorkbookVisibility(ref Stream stream)
        {
            using (var spreadsheetDocument = SpreadsheetDocument.Open(stream, true))
            {
                var bookViews = spreadsheetDocument.WorkbookPart.Workbook.BookViews;
                foreach (var bookView in bookViews)
                {
                    var workBookView = (WorkbookView) bookView;
                    if (workBookView.Visibility.Value == VisibilityValues.Hidden ||
                        workBookView.Visibility.Value == VisibilityValues.Hidden)
                        workBookView.Visibility.Value = VisibilityValues.Visible;
                }

                spreadsheetDocument.WorkbookPart.Workbook.Save();
            }
        }
        #endregion

        #region ExtractFromExcelBinaryFormat
        /// <summary>
        /// This method saves all the Excel embedded binary objects from the <see cref="inputFile"/> to the
        /// <see cref="outputFolder"/>
        /// </summary>
        /// <param name="inputFile">The binary Excel file</param>
        /// <param name="outputFolder">The output folder</param>
        /// <param name="storageName">The complete or part of the name from the storage that needs to be saved</param>
        /// <returns></returns>
        /// <exception cref="OEFileIsPasswordProtected">Raised when the <see cref="inputFile"/> is password protected</exception>
        /// <exception cref="OEFileIsCorrupt">Raised when the file is corrupt</exception>
        private List<string> ExtractFromExcelBinaryFormat(string inputFile, string outputFolder, string storageName)
        {
            var compoundFile = new CompoundFile(inputFile);

            if (ExcelBinaryFormatIsPasswordProtected(compoundFile))
                throw new OEFileIsPasswordProtected("The file '" + Path.GetFileName(inputFile) + "' is password protected");

            var result = new List<string>();

            foreach (var child in compoundFile.RootStorage.Children)
            {
                var childStorage = child as CFStorage;
                if (childStorage == null) continue;
                if (!childStorage.Name.StartsWith(storageName)) continue;

                var extractedFileName = ExtractFromStorageNode(childStorage, outputFolder);
                if (extractedFileName != null)
                    result.Add(extractedFileName);
            }

            return result;
        }
        #endregion

        #region PowerPointBinaryFormatIsPasswordProtected
        /// <summary>
        /// Returns true when the binary PowerPoint file is password protected
        /// </summary>
        /// <param name="compoundFile"></param>
        /// <returns></returns>
        private static bool PowerPointBinaryFormatIsPasswordProtected(CompoundFile compoundFile)
        {
            if (!compoundFile.RootStorage.ExistsStream("Current User")) return false;
            var stream = compoundFile.RootStorage.GetStream("Current User") as CFStream;
            if (stream == null) return false;

            using (var memoryStream = new MemoryStream(stream.GetData()))
            using (var binaryReader = new BinaryReader(memoryStream))
            {
                var verAndInstance = binaryReader.ReadUInt16();
                // ReSharper disable UnusedVariable
                // We need to read these fields to get to the correct location in the Current User stream
                var version = verAndInstance & 0x000FU;         // first 4 bit of field verAndInstance
                var instance = (verAndInstance & 0xFFF0U) >> 4; // last 12 bit of field verAndInstance
                var typeCode = binaryReader.ReadUInt16();
                var size = binaryReader.ReadUInt32();
                var size1 = binaryReader.ReadUInt32();
                // ReSharper restore UnusedVariable
                var headerToken = binaryReader.ReadUInt32();

                switch (headerToken)
                {
                    // Not encrypted
                    case 0xE391C05F:
                        return false;

                    // Encrypted
                    case 0xF3D1C4DF:
                        return true;

                    default:
                        return false;
                }
            }
        }
        #endregion

        #region ExtractFromPowerPointBinaryFormat
        /// <summary>
        /// This method saves all the PowerPoint embedded binary objects from the <see cref="inputFile"/> to the
        /// <see cref="outputFolder"/>
        /// </summary>
        /// <param name="inputFile">The binary PowerPoint file</param>
        /// <param name="outputFolder">The output folder</param>
        /// <returns></returns>
        /// <exception cref="OEFileIsPasswordProtected">Raised when the <see cref="inputFile"/> is password protected</exception>
        private static List<string> ExtractFromPowerPointBinaryFormat(string inputFile, string outputFolder)
        {
            var compoundFile = new CompoundFile(inputFile);

            if (PowerPointBinaryFormatIsPasswordProtected(compoundFile))
                throw new OEFileIsPasswordProtected("The file '" + Path.GetFileName(inputFile) + "' is password protected");

            var result = new List<string>();

            if (!compoundFile.RootStorage.ExistsStream("PowerPoint Document")) return result;
            var stream = compoundFile.RootStorage.GetStream("PowerPoint Document") as CFStream;
            if (stream == null) return result;

            using (var memoryStream = new MemoryStream(stream.GetData()))
            using (var binaryReader = new BinaryReader(memoryStream))
            {
                while (binaryReader.BaseStream.Position != memoryStream.Length)
                {
                    var verAndInstance = binaryReader.ReadUInt16();
                    // ReSharper disable once UnusedVariable
                    var version = verAndInstance & 0x000FU;         // first 4 bit of field verAndInstance
                    var instance = (verAndInstance & 0xFFF0U) >> 4; // last 12 bit of field verAndInstance

                    var typeCode = binaryReader.ReadUInt16();
                    var size = binaryReader.ReadUInt32();
                    //var isContainer = (version == 0xF);

                    // Embedded OLE objects start with code 4045
                    if (typeCode == 4113)
                    {
                        if (instance == 0)
                        {
                            // Uncompressed
                            var bytes = binaryReader.ReadBytes((int) size);

                            // Check if the ole object is another compound storage node with a package stream
                            if (bytes[0] == 0xD0 && bytes[1] == 0xCF)
                                bytes = CheckIfIsCompoundFileWithPackageStream(bytes);
                            result.Add(SaveByteArrayToFile(bytes, outputFolder + "Embedded object"));
                        }
                        else
                        {
                            var decompressedSize = binaryReader.ReadUInt32();
                            var data = binaryReader.ReadBytes((int) size - 4);
                            var compressedMemoryStream = new MemoryStream(data);

                            // skip the first 2 bytes
                            compressedMemoryStream.ReadByte();
                            compressedMemoryStream.ReadByte();

                            // Decompress the bytes
                            var decompressedBytes = new byte[decompressedSize];
                            var deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress, true);
                            deflateStream.Read(decompressedBytes, 0, decompressedBytes.Length);

                            // Check if the ole object is another compound storage node with a package stream
                            if (decompressedBytes[0] == 0xD0 && decompressedBytes[1] == 0xCF)
                                decompressedBytes = CheckIfIsCompoundFileWithPackageStream(decompressedBytes);

                            result.Add(SaveByteArrayToFile(decompressedBytes, outputFolder + "Embedded object"));
                        }
                    }
                    else
                        binaryReader.BaseStream.Position += size;
                }
            }

            return result;
        }
        #endregion

        #region CheckIfIsCompoundFileWithPackageStream
        /// <summary>
        /// Checks if the <see cref="bytes"/> is a compound file and if so then tries to extract
        /// the package stream from it. If it fails it will return the original <see cref="bytes"/>
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private static byte[] CheckIfIsCompoundFileWithPackageStream(byte[] bytes)
        {
            try
            {
                using (var memoryStream = new MemoryStream(bytes))
                {
                    var compoundFile = new CompoundFile(memoryStream);
                    if (!compoundFile.RootStorage.ExistsStream("Package"))
                        return bytes;

                    var package = compoundFile.RootStorage.GetStream("Package");
                    return package.GetData();
                }
            }
            catch (Exception)
            {
                return bytes;
            }    
        }
        #endregion

        #region ExtractFromOfficeOpenXmlFormat
        /// <summary>
        /// Extracts all the embedded object from the Office Open XML <see cref="inputFile"/> to the 
        /// <see cref="outputFolder"/> and returns the files with full path as a list of strings
        /// </summary>
        /// <param name="inputFile">The Office Open XML format file</param>
        /// <param name="embeddingPartString">The folder in the Office Open XML format (zip) file</param>
        /// <param name="outputFolder">The output folder</param>
        /// <returns>List with files or en empty list when there are nog embedded files</returns>
        public List<string> ExtractFromOfficeOpenXmlFormat(string inputFile, string embeddingPartString, string outputFolder)
        {
            var result = new List<string>();
            var package = Package.Open(inputFile);

            // Get the embedded files names. 
            foreach (var packagePart in package.GetParts())
            {
                if (packagePart.Uri.ToString().StartsWith(embeddingPartString))
                {
                    var stream = packagePart.GetStream();
                    var fileName = outputFolder + packagePart.Uri.ToString().Remove(0, embeddingPartString.Length);

                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        if (fileName.ToUpperInvariant().Contains("OLEOBJECT"))
                        {
                            result.Add(ExtractFileFromOle10Native(memoryStream.ToArray(), outputFolder));
                        }
                        else
                        {
                            fileName = FileManager.FileExistsMakeNew(fileName);
                            File.WriteAllBytes(fileName, memoryStream.ToArray());
                            result.Add(fileName);
                        }
                    }
                }
            }
            package.Close();

            return result;
        }
        #endregion

        #region ExtractFromStorageNode
        /// <summary>
        /// This method will extract and save the data from the given <see cref="storage"/> node to the <see cref="outputFolder"/>
        /// </summary>
        /// <param name="storage">The <see cref="CFStorage"/> node</param>
        /// <param name="outputFolder">The outputFolder</param>
        /// <returns></returns>
        /// <exception cref="OEFileIsPasswordProtected">Raised when a WordDocument, WorkBook or PowerPoint Document stream is password protected</exception>
        private string ExtractFromStorageNode(CFStorage storage, string outputFolder)
        {
            // Embedded objects can be stored in 4 ways
            // - As a CONTENT stream
            // - As a Package
            // - As an Ole10Native object
            // - Embedded into the same compound file
            if (storage.ExistsStream("CONTENTS"))
            {
                var contents = storage.GetStream("CONTENTS");
                if (contents.Size > 0)
                    return SaveByteArrayToFile(contents.GetData(), outputFolder + "Embedded object");
            }
            else if (storage.ExistsStream("Package"))
            {
                var package = storage.GetStream("Package");
                if (package.Size > 0)
                    return SaveByteArrayToFile(package.GetData(), outputFolder + "Embedded object");
            }
            else if (storage.ExistsStream("\x01Ole10Native"))
            {
                var ole10Native = storage.GetStream("\x01Ole10Native");
                if (ole10Native.Size > 0)
                    return ExtractFileFromOle10Native(ole10Native.GetData(), outputFolder);
            }
            else if (storage.ExistsStream("WordDocument"))
            {
                // The embedded object is a Word file
                var tempFileName = FileManager.FileExistsMakeNew(outputFolder + "Embedded Word document.doc");
                SaveStorageTreeToCompoundFile(storage, tempFileName);
                return tempFileName;
            }
            else if (storage.ExistsStream("Workbook"))
            {
                // The embedded object is an Excel file   
                var tempFileName = FileManager.FileExistsMakeNew(outputFolder + "Embedded Excel document.xls");
                SaveStorageTreeToCompoundFile(storage, tempFileName);
                return tempFileName;
            }
            else if (storage.ExistsStream("PowerPoint Document"))
            {
                // The embedded object is a PowerPoint file
                var tempFileName = outputFolder + FileManager.FileExistsMakeNew("Embedded PowerPoint document.ppt");
                SaveStorageTreeToCompoundFile(storage, tempFileName);
                return tempFileName;
            }

            return null;
        }
        #endregion

        #region SaveStorageTreeToCompoundFile
        /// <summary>
        /// This will save the complete tree from the given <see cref="storage"/> to a new <see cref="CompoundFile"/>
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="fileName">The filename with path for the new compound file</param>
        private void SaveStorageTreeToCompoundFile(CFStorage storage, string fileName)
        {
            var compoundFile = new CompoundFile();
            GetStorageChain(compoundFile.RootStorage, storage);
            compoundFile.Save(fileName);
        }

        /// <summary>
        /// Returns the complete tree with all the <see cref="CFStorage"/> and <see cref="CFStream"/> children
        /// </summary>
        /// <param name="rootStorage"></param>
        /// <param name="storage"></param>
        private void GetStorageChain(CFStorage rootStorage, CFStorage storage)
        {
            foreach (var child in storage.Children)
            {
                if (child.IsStorage)
                {
                    var newRootStorage = rootStorage.AddStorage(child.Name);
                    GetStorageChain(newRootStorage, child as CFStorage);
                }
                else if (child.IsStream)
                {
                    var childStream = child as CFStream;
                    if (childStream == null) continue;
                    var stream = rootStorage.AddStream(child.Name);
                    var bytes = childStream.GetData();
                    stream.SetData(bytes);
                }
            }
        }
        #endregion

        #region SaveByteArrayToFile
        /// <summary>
        /// Saves the <see cref="data"/> byte array to the <see cref="outputFile"/>
        /// </summary>
        /// <param name="data">The stream as byte array</param>
        /// <param name="outputFile">The output filename with path</param>
        /// <returns></returns>
        private static string SaveByteArrayToFile(byte[] data, string outputFile)
        {
            // Because the data is stored in a stream we have no name for it so we
            // have to check the magic bytes to see with what kind of file we are dealing
            var fileType = FileTypeSelector.GetFileTypeFileInfo(data);
            if (fileType != null && !string.IsNullOrEmpty(fileType.Extension))
                outputFile += "." + fileType.Extension;

            // Check if the output file already exists and if so make a new one
            outputFile = FileManager.FileExistsMakeNew(outputFile);

            if (fileType != null)
            {
                switch (fileType.Extension.ToUpperInvariant())
                {
                    case "XLS":
                        using (var memoryStream = new MemoryStream(data))
                        {
                            var compoundFile = new CompoundFile(memoryStream);
                            ExcelBinaryFormatSetWorkbookVisibility(ref compoundFile);
                            compoundFile.Save(outputFile);
                        }
                        break;
                        
                    case "XLSX":
                        using (var memoryStream = new MemoryStream(data))
                        {
                            ExcelOpenXmlFormatSetWorkbookVisibility(memoryStream);
                        }
                        break;
                }
            }
            else
                File.WriteAllBytes(outputFile, data);

            return outputFile;
        }
        #endregion

        #region ExtractFileFromOle10Native
        /// <summary>
        /// Extract the file from the Ole10Native container and saves it to the outputfolder
        /// </summary>
        /// <param name="ole10Native">The Ole10Native object as an byte array</param>
        /// <param name="outputFolder">The output folder</param>
        /// <returns>The filename with path from the extracted file</returns>
        private static string ExtractFileFromOle10Native(byte[] ole10Native, string outputFolder)
        {
            // Convert the byte array to a stream
            using (Stream oleStream = new MemoryStream(ole10Native))
            {
                // The name of the file start at postion 7 so move to there
                oleStream.Position = 6;
                var tempFileName = new char[260];

                // Read until we find a null character
                int i;
                var chr = new byte[1];
                for (i = 0; i < 260; i++)
                {
                    oleStream.Read(chr, 0, 1);
                    tempFileName[i] = (char) chr[0];
                    if (chr[0] == 0)
                        break;
                }

                var fileName = new string(tempFileName, 0, i);

                // We don't need this but we need to read it to know where we
                // are located in the stream
                var tempOriginalFilePath = new char[260];
                for (i = 0; i < 260; i++)
                {
                    oleStream.Read(chr, 0, 1);
                    tempOriginalFilePath[i] = (char) chr[0];
                    if (chr[0] == 0)
                        break;
                }

                // We need to skip the next four bytes
                oleStream.Position += 4;

                // Read the tempory path size
                var size = new byte[4];
                oleStream.Read(size, 0, 4);
                var tempPathSize = BitConverter.ToInt32(size, 0);

                // Move the position in the stream after the temp path
                oleStream.Position += tempPathSize;

                // Read the next four bytes for the length of the data
                oleStream.Read(size, 0, 4);
                var fileSize = BitConverter.ToInt32(size, 0);

                // And finaly we have come to the original file
                var fileData = new byte[fileSize];
                oleStream.Read(fileData, 0, fileSize);

                // Check if the output file already exists and if so make a new one
                fileName = outputFolder + fileName;
                fileName = FileManager.FileExistsMakeNew(fileName);

                File.WriteAllBytes(fileName, fileData);
                return fileName;
            }
        }
        #endregion
    }
}
