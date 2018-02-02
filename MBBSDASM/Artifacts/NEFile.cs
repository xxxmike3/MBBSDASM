﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MBBSDASM.Enums;

namespace MBBSDASM.Artifacts
{
    /// <summary>
    ///     Class Represents the Parsed Content of a 16-bit NE Format EXE/DLL file
    /// </summary>
    public class NEFile
    {
        //File Metadata
        public string Path { get; set; }
        public string FileName { get; set; }
        
        //Contains the entire contents of the file to be disassembled
        public readonly byte[] FileContent;
        
        //Artifacts of the NE Header
        public NEHeader Header;
        public List<Segment> SegmentTable;
        public List<ResourceRecord> ResourceTable;
        public List<ResidentName> ResidentNameTable;
        public List<ModuleReference> ModuleReferenceTable;
        public List<ImportedName> ImportedNameTable;
        public List<Entry> EntryTable;
        public List<NonResidentName> NonResidentNameTable;

        public NEFile(string file)
        {
            FileContent = File.ReadAllBytes(file);
            var f = new FileInfo(file);
            Path = f.DirectoryName + "\\";
            FileName = f.Name;
            Load();
        }

        private void Load()
        {
            var data = new Span<byte>(FileContent);

            //Validate Old-style header
            if (data.Length < 148 || data[0] != 'M' && data[1] != 'Z')
                throw new Exception("Invalid Old-Style Header");           
            
            //Locate Windows Header
            ushort windowsHeaderOffset;
            if (data[0x18] >= 0x40)
            {
                windowsHeaderOffset = BitConverter.ToUInt16(FileContent, 0x3C);
            }
            else
            {
                throw new Exception("Unable to locate Windows Header location");
            }

            //Load Windows Header
            Header = new NEHeader(data.Slice(windowsHeaderOffset, 0x3F).ToArray()) { FileOffset = windowsHeaderOffset };
            
            //Adjust Offsets According to Spec (Offset from beginning of Windows Header, not file)
            Header.SegmentTableOffset += windowsHeaderOffset;
            Header.ResourceTableOffset += windowsHeaderOffset;
            Header.ResidentNameTableOffset += windowsHeaderOffset;
            Header.ModleReferenceTableOffset += windowsHeaderOffset;
            Header.ImportedNamesTableOffset += windowsHeaderOffset;
            Header.EntryTableOffset += windowsHeaderOffset;

            //Load Segment Table
            SegmentTable = new List<Segment>(Header.SegmentTableEntries);
            for (var i = 0; i < Header.SegmentTableEntries; i++)
            {
                //Load Segment Header (8 bytes per record)
                var segment =
                    new Segment(data.Slice(Header.SegmentTableOffset + (i * 8), 8).ToArray())
                    {
                        Ordinal = (ushort) (i + 1)
                    };
                segment.Offset <<= Header.LogicalSectorAlignmentShift;
                
                //Attach Segment Data
                segment.Data = data.Slice((int)segment.Offset, segment.Length).ToArray();
                
                //Attach Relocation Records
                if (segment.Flags.Contains(EnumSegmentFlags.HasRelocationInfo))
                {
                    var relocationInfoCursor = (int)segment.Offset + segment.Length;
                    var relocationRecordEntries = BitConverter.ToUInt16(FileContent, relocationInfoCursor);
                    relocationInfoCursor += 2;
                    var records = new List<RelocationRecord>();
                    for (var j = 0; j < relocationRecordEntries; j++)
                    {
                        records.Add(new RelocationRecord {Data = data.Slice(relocationInfoCursor + j * 8, 8).ToArray()});
                    }
                    segment.RelocationRecords = records;
                }
                SegmentTable.Add(segment);
            }
            
            //Load Resource Table
            ResourceTable = new List<ResourceRecord>();
            //TODO -- Resource Table isn't used by MBBS modules so we'll skip loading this for now
            //TODO -- Implement this in a future version
            
            //Load Resident Name Table
            ResidentNameTable = new List<ResidentName>();
            for (var i = 0; i < Header.ModleReferenceTableOffset; i +=2)
            {
                var residentName = new ResidentName();
                var residentNameLength = data[Header.ResidentNameTableOffset + i];
                
                //End of Names
                if (residentNameLength == 0)
                    break;
                
                i++;
                residentName.Name =
                    Encoding.ASCII.GetString(data.Slice(Header.ResidentNameTableOffset + i, residentNameLength)
                        .ToArray());
                i += residentNameLength;
                residentName.IndexIntoEntryTable = BitConverter.ToUInt16(FileContent, Header.ResidentNameTableOffset + i);
                ResidentNameTable.Add(residentName);
            }

            //Load Module & Imported Name Reference Tables
            ModuleReferenceTable = new List<ModuleReference>(Header.ModuleReferenceTableEntries);
            ImportedNameTable = new List<ImportedName>();
            for (var i = 0; i < Header.ModuleReferenceTableEntries; i++)
            {
                var nameOffset =
                    BitConverter.ToUInt16(FileContent, Header.ModleReferenceTableOffset + i * 2);

                nameOffset += Header.ImportedNamesTableOffset;
                
                var module = new ModuleReference();
                var importedName = new ImportedName() { Offset = nameOffset};

                var name = Encoding.ASCII.GetString(data.Slice(nameOffset + 1, data[nameOffset]).ToArray());

                module.Name = name;
                importedName.Name = name;
                importedName.Ordinal = (ushort)(i + 1); //Ordinal Index in Resource Tables start with 1
                
                ModuleReferenceTable.Add(module);
                ImportedNameTable.Add(importedName);
            }
            
            //Load Entry Table
            EntryTable = new List<Entry>(data[Header.EntryTableOffset]);
            
            //Value of 0 denotes no segment data
            if (data[Header.EntryTableOffset] > 0)
            {
                var entryByteOffset = 0;
                ushort entryOrdinal = 1;
                while (Header.EntryTableOffset + entryByteOffset  < Header.NonResidentNameTableOffset)
                {
                    //0xFF is moveable (6 bytes), anything else is fixed as it becomes the segment number
                    var entryCount = data[Header.EntryTableOffset + entryByteOffset]; 
                    var entrySegment = data[Header.EntryTableOffset + entryByteOffset + 1];

                    if (entryCount == 1  && entrySegment == 0)
                    {
                        entryByteOffset += 2;
                        entryOrdinal += 1;
                        continue;
                    }

                    var entrySize = entrySegment == 0xFF ? 6 : 3;

                    for (var i = 0; i < entryCount; i++)
                    {
                        var entry = new Entry { SegmentNumber = entrySegment};
                        if (entrySize == 3)
                        {
                            entry.Flag = data[Header.EntryTableOffset + entryByteOffset + 2 + entrySize * i];
                            entry.Offset = BitConverter.ToUInt16(FileContent,
                                Header.EntryTableOffset + entryByteOffset + 3 + entrySize * i);
                            entry.SegmentNumber = entrySegment;
                            entry.Ordinal = entryOrdinal;   //First Entry is the Resident Name table is the module name, so we shift the ordinals by 1 to line up
                        }
                        else
                        {
                            entry.Flag = data[Header.EntryTableOffset + entryByteOffset + 2 + entrySize * i];
                            entry.SegmentNumber = data[Header.EntryTableOffset + entryByteOffset + 5 + (entrySize * i)];
                            entry.Offset =
                                BitConverter.ToUInt16(FileContent,
                                    Header.EntryTableOffset + entryByteOffset + 6 + entrySize * i);
                        }
                        entryOrdinal++;
                        EntryTable.Add(entry);
                    }

                    entryByteOffset += (entryCount * entrySize) + 2;
                }
            }
            
            //Load Non-Resident Name Table
            NonResidentNameTable = new List<NonResidentName>();
            for (var i = (int)Header.NonResidentNameTableOffset; i < (Header.NonResidentNameTableOffset + Header.NonResidentNameTableLength); i += 2)
            {
                var nameLength = data[i];
                i++;
                var name = Encoding.ASCII.GetString(data.Slice(i, nameLength).ToArray());
                i += nameLength;
                var indexIntoEntryTable = BitConverter.ToUInt16(FileContent, i);
                NonResidentNameTable.Add(new NonResidentName() { Name = name, IndexIntoEntryTable = indexIntoEntryTable});
            }
        }
    }
}