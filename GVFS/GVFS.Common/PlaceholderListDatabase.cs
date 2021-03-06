﻿using GVFS.Common.FileSystem;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;

namespace GVFS.Common
{
    public class PlaceholderListDatabase : FileBasedCollection
    {
        private const char PathTerminator = '\0';

        // This list holds entries that would otherwise be lost because WriteAllEntriesAndFlush has not been called, but a file 
        // snapshot has been taken using GetAllEntries.
        // See the unit test PlaceholderDatabaseTests.HandlesRaceBetweenAddAndWriteAllEntries for example
        //
        // With this list, we can no longer call GetAllEntries without a matching WriteAllEntries afterwards.
        // 
        // This list must always be accessed from inside one of FileBasedCollection's synchronizedAction callbacks because
        // there is race potential between creating the queue, adding to the queue, and writing to the data file.
        private List<PlaceholderDataEntry> placeholderDataEntries;
        
        private PlaceholderListDatabase(ITracer tracer, PhysicalFileSystem fileSystem, string dataFilePath)
            : base(tracer, fileSystem, dataFilePath, collectionAppendsDirectlyToFile: true)
        {
        }

        /// <summary>
        /// The EstimatedCount is "estimated" because it's simply (# adds - # deletes).  There is nothing to prevent
        /// multiple adds or deletes of the same path from being double counted
        /// </summary>
        public int EstimatedCount { get; private set; }

        public static bool TryCreate(ITracer tracer, string dataFilePath, PhysicalFileSystem fileSystem, out PlaceholderListDatabase output, out string error)
        {
            PlaceholderListDatabase temp = new PlaceholderListDatabase(tracer, fileSystem, dataFilePath);

            // We don't want to cache placeholders so this just serves to validate early and populate count.
            if (!temp.TryLoadFromDisk<string, string>(
                temp.TryParseAddLine,
                temp.TryParseRemoveLine,
                (key, value) => temp.EstimatedCount++,
                out error))
            {
                temp = null;
                output = null;
                return false;
            }

            error = null;
            output = temp;
            return true;
        }
        
        public void AddAndFlush(string path, string sha)
        {
            try
            {
                this.WriteAddEntry(
                    path + PathTerminator + sha,
                    () =>
                    {
                        this.EstimatedCount++;
                        if (this.placeholderDataEntries != null)
                        {
                            this.placeholderDataEntries.Add(new PlaceholderDataEntry(path, sha));
                        }
                    });
            }
            catch (Exception e)
            {
                throw new FileBasedCollectionException(e);
            }
        }

        public void RemoveAndFlush(string path)
        {
            try
            {
                this.WriteRemoveEntry(
                    path,
                    () =>
                    {
                        this.EstimatedCount--;
                        if (this.placeholderDataEntries != null)
                        {
                            this.placeholderDataEntries.Add(new PlaceholderDataEntry(path));
                        }
                    });
            }
            catch (Exception e)
            {
                throw new FileBasedCollectionException(e);
            }
        }

        public List<PlaceholderData> GetAllEntries()
        {
            try
            {
                List<PlaceholderData> output = new List<PlaceholderData>(Math.Max(1, this.EstimatedCount));

                string error;
                if (!this.TryLoadFromDisk<string, string>(
                    this.TryParseAddLine,
                    this.TryParseRemoveLine,
                    (key, value) => output.Add(new PlaceholderData(path: key, sha: value)),
                    out error,
                    () =>
                    {
                        if (this.placeholderDataEntries != null)
                        {
                            throw new InvalidOperationException("PlaceholderListDatabase should always flush queue placeholders using WriteAllEntriesAndFlush before calling GetAllEntries again.");
                        }

                        this.placeholderDataEntries = new List<PlaceholderDataEntry>();
                    }))
                {
                    throw new InvalidDataException(error);
                }

                return output;
            }
            catch (Exception e)
            {
                throw new FileBasedCollectionException(e);
            }
        }

        public void WriteAllEntriesAndFlush(IEnumerable<PlaceholderData> updatedPlaceholders)
        {
            try
            {
                this.WriteAndReplaceDataFile(() => this.GenerateDataLines(updatedPlaceholders));
            }
            catch (Exception e)
            {
                throw new FileBasedCollectionException(e);
            }
        }

        private IEnumerable<string> GenerateDataLines(IEnumerable<PlaceholderData> updatedPlaceholders)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            this.EstimatedCount = 0;
            foreach (PlaceholderData updated in updatedPlaceholders)
            {
                if (keys.Add(updated.Path))
                {
                    this.EstimatedCount++;
                }

                yield return this.FormatAddLine(updated.Path + PathTerminator + updated.Sha);
            }

            if (this.placeholderDataEntries != null)
            {
                foreach (PlaceholderDataEntry entry in this.placeholderDataEntries)
                {
                    if (entry.DeleteEntry)
                    {
                        if (keys.Remove(entry.Path))
                        {
                            this.EstimatedCount--;
                            yield return this.FormatRemoveLine(entry.Path);
                        }
                    }
                    else
                    {
                        if (keys.Add(entry.Path))
                        {
                            this.EstimatedCount++;
                        }

                        yield return this.FormatAddLine(entry.Path + PathTerminator + entry.Sha);
                    }
                }

                this.placeholderDataEntries = null;
            }
        }

        private bool TryParseAddLine(string line, out string key, out string value, out string error)
        {
            // Expected: <Placeholder-Path>\0<40-Char-SHA1>
            int idx = line.IndexOf(PathTerminator);
            if (idx < 0)
            {
                key = null;
                value = null;
                error = "Add line missing path terminator: " + line;
                return false;
            }

            if (idx + 1 + GVFSConstants.ShaStringLength != line.Length)
            {
                key = null;
                value = null;
                error = "Invalid SHA1 length: " + line;
                return false;
            }

            key = line.Substring(0, idx);
            value = line.Substring(idx + 1, GVFSConstants.ShaStringLength);

            error = null;
            return true;
        }

        private bool TryParseRemoveLine(string line, out string key, out string error)
        {
            // The key is a path taking the entire line.
            key = line;
            error = null;
            return true;
        }

        public class PlaceholderData
        {
            public PlaceholderData(string path, string sha)
            {
                this.Path = path;
                this.Sha = sha;
            }

            public string Path { get; }
            public string Sha { get; }

            public bool IsFolder
            {
                get { return this.Sha == GVFSConstants.AllZeroSha; }
            }
        }

        private class PlaceholderDataEntry
        {
            public PlaceholderDataEntry(string path, string sha)
            {
                this.Path = path;
                this.Sha = sha;
                this.DeleteEntry = false;
            }

            public PlaceholderDataEntry(string path)
            {
                this.Path = path;
                this.Sha = null;
                this.DeleteEntry = true;
            }

            public string Path { get; }
            public string Sha { get; }
            public bool DeleteEntry { get; }
        }
    }
}
