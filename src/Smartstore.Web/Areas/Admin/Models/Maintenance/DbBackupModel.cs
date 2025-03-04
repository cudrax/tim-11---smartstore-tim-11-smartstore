﻿using System;
using Smartstore.IO;
using Smartstore.Utilities;
using Smartstore.Web.Modelling;

namespace Smartstore.Admin.Models.Maintenance
{
    [LocalizedDisplay("Admin.System.Maintenance.DbBackup.")]
    public class DbBackupModel : ModelBase
    {
        public DbBackupModel(IFile backup)
        {
            Guard.NotNull(backup, nameof(backup));

            Backup = backup;
        }

        public IFile Backup { get; }
        public string DownloadUrl { get; set; }

        [LocalizedDisplay("Admin.Common.FileName")]
        public string Name => Backup.Name;

        [LocalizedDisplay("Common.CreatedOn")]
        public DateTime CreatedOn { get; set; }

        [LocalizedDisplay("Admin.Common.FileSize")]
        public string LengthString
            => Prettifier.HumanizeBytes(Backup.Length);

        [LocalizedDisplay("Admin.System.SystemInfo.AppVersion")]
        public string Version { get; set; }

        [LocalizedDisplay("*IsCurrentVersion")]
        public bool IsCurrentVersion { get; set; }
    }
}
