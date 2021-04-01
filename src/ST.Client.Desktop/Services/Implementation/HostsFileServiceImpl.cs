﻿using System.Application.UI.Resx;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using static System.Application.Services.IHostsFileService;

namespace System.Application.Services.Implementation
{
    internal sealed class HostsFileServiceImpl : IHostsFileService
    {
        readonly IDesktopPlatformService s;

        public HostsFileServiceImpl(IDesktopPlatformService s)
        {
            this.s = s;
        }

        #region Mark

        internal const string MarkStart = "# Steam++ Start";
        internal const string MarkEnd = "# Steam++ End";
        internal const string BackupMarkStart = "# Steam++ Backup Start";
        internal const string BackupMarkEnd = "# Steam++ Backup End";

        /// <summary>
        /// 根据行切割数组获取标记值
        /// </summary>
        /// <param name="line_split_array"></param>
        /// <returns></returns>
        static string? GetMarkValue(string[] line_split_array)
        {
            if (line_split_array.Length == 3 || line_split_array.Length == 4)
            {
                var value = string.Join(' ', line_split_array);
                if (line_split_array.Length == 3)
                {
                    if (string.Equals(value, MarkStart, StringComparison.OrdinalIgnoreCase))
                    {
                        return MarkStart;
                    }
                    if (string.Equals(value, MarkEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        return MarkEnd;
                    }
                }
                else /*if (array.Length == 4)*/
                {
                    if (string.Equals(value, BackupMarkStart, StringComparison.OrdinalIgnoreCase))
                    {
                        return BackupMarkStart;
                    }
                    if (string.Equals(value, BackupMarkEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        return BackupMarkEnd;
                    }
                }
            }
            return default;
        }

        #endregion

        #region FileVerify

        /// <summary>
        /// 最大支持文件大小，50MB
        /// </summary>
        const long MaxFileLength = 52428800;

        /// <summary>
        /// 尝试开始操作前
        /// </summary>
        /// <param name="message"></param>
        /// <param name="fileInfo"></param>
        /// <param name="removeReadOnly"></param>
        /// <param name="checkReadOnly"></param>
        /// <param name="checkMaxLength"></param>
        /// <returns></returns>
        bool TryOperation([NotNullWhen(false)] out string? message,
            out FileInfo fileInfo,
            out bool removeReadOnly,
            bool checkReadOnly = false,
            bool checkMaxLength = true)
        {
            removeReadOnly = false;
            fileInfo = new FileInfo(s.HostsFilePath);
            if (!fileInfo.Exists)
            {
                message = "hosts file was not found";
                return false;
            }
            if (checkMaxLength)
            {
                if (fileInfo.Length > MaxFileLength)
                {
                    message = "hosts file is too large";
                    return false;
                }
            }
            if (checkReadOnly)
            {
                var attr = fileInfo.Attributes;
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    fileInfo.Attributes = attr & ~FileAttributes.ReadOnly;
                    removeReadOnly = true;
                }
            }
            message = null;
            return true;
        }

        /// <summary>
        /// 设置文件只读属性
        /// </summary>
        /// <param name="fileInfo"></param>
        static void SetReadOnly(FileInfo fileInfo)
        {
            try
            {
                var attr = fileInfo.Attributes;
                if (!attr.HasFlag(FileAttributes.ReadOnly))
                {
                    attr |= FileAttributes.ReadOnly;
                    fileInfo.Attributes = attr;
                }
            }
            catch
            {
            }
        }

        #endregion

        #region Handle

        static string[] GetLineSplitArray(string line_value) => line_value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// 处理一行数据
        /// </summary>
        /// <param name="line_num"></param>
        /// <param name="domains"></param>
        /// <param name="line_value"></param>
        /// <param name="line_split_array"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        static bool HandleLine(int line_num, HashSet<string> domains, string line_value, out string[] line_split_array, Func<string[], bool>? func = null)
        {
            line_split_array = GetLineSplitArray(line_value);
            var r = func?.Invoke(line_split_array) ?? true;
            if (!r) return false;
            if (line_split_array.Length < 2) return false;
            if (line_split_array[0].StartsWith('#')) return false;
            if (line_split_array.Length > 2 && !line_split_array[2].StartsWith('#')) return false;
            if (!domains.Add(line_split_array[1]))
                throw new Exception($"hosts file line {line_num} duplicate");
            return true;
        }

        OperationResult HandleHosts(bool isUpdateOrRemove, IReadOnlyDictionary<string, string>? hosts = null)
        {
            var result = new OperationResult(OperationResultType.Error, AppResources.Hosts_WirteError);
            if (!TryOperation(out var errmsg, out var fileInfo, out var removeReadOnly, checkReadOnly: true))
            {
                result.Message = errmsg;
                return result;
            }

            try
            {
                var has_hosts = hosts.Any_Nullable();
                if (isUpdateOrRemove && !has_hosts) throw new InvalidOperationException();

                StringBuilder stringBuilder = new();
                HashSet<string> markLength = new(); // mark 标志
                Dictionary<string, string> insert_mark_datas = new(); // 直接插入标记区数据
                Dictionary<string, (int line_num, string line_value)> backup_insert_mark_datas = new(); // 备份插入标记区数据，项已存在的情况下
                Dictionary<string, (int line_num, string line_value)> backup_datas = new(); // 备份区域数据

                using (var fileReader = fileInfo.OpenText())
                {
                    int line_num = 0;
                    HashSet<string> domains = new(); // 域名唯一检查
                    while (true)
                    {
                        line_num++;

                        var line_value = fileReader.ReadLine();
                        if (line_value == null) break;

                        var not_append = false;
                        var is_mark = false;
                        var is_effective_value = HandleLine(line_num, domains, line_value, out var line_split_array, line_split_array =>
                        {
                            var mark = GetMarkValue(line_split_array);
                            if (mark == null) return true;
                            is_mark = true;
                            if (!markLength.Add(mark)) throw new Exception($"hosts file mark duplicate, value: {mark}");
                            return false;
                        });
                        if (is_mark) continue; // 当前行是 mark 标志，跳过
                        if (!is_effective_value) goto append; // 当前行是无效值，直接写入
                        if (line_split_array.Length == 3 && line_split_array[2] == "#Steam++") // Compat V1
                        {
                            domains.Remove(line_split_array[1]);
                            continue;
                        }
                        string ip, domain;
                        if (markLength.Contains(BackupMarkStart) && !markLength.Contains(BackupMarkEnd))
                        {
                            if (line_split_array.Length >= 2 && line_split_array[0].StartsWith('#') && int.TryParse(line_split_array[0].TrimStart('#'), out var bak_line_num)) // #{line_num} {line_value}
                            {
                                var bak_line_split_array = line_split_array.AsSpan()[1..];
                                if (bak_line_split_array.Length >= 2)
                                {
                                    domain = bak_line_split_array[1];
                                    backup_datas.TryAdd(domain, (bak_line_num, line_value));
                                }
                            }
                            continue;
                        }
                        ip = line_split_array[0];
                        domain = line_split_array[1];
                        var match_domain = has_hosts && hosts.ContainsKey(domain); // 与要修改的项匹配
                        if (markLength.Contains(MarkStart) && !markLength.Contains(MarkEnd)) // 在标记区域内
                        {
                            if (match_domain)
                            {
                                if (isUpdateOrRemove) // 更新值
                                {
                                    ip = hosts[domain];
                                }
                                else // 删除值
                                {
                                    continue;
                                }
                            }
                            insert_mark_datas.TryAdd(domain, ip);
                            continue;
                        }
                        else // 在标记区域外
                        {
                            if (match_domain)
                            {
                                if (isUpdateOrRemove) // 更新值
                                {
                                    insert_mark_datas.TryAdd(domain, ip);
                                }
                                backup_insert_mark_datas.TryAdd(domain, (line_num, line_value));
                                continue;
                            }
                        }

                        if (not_append) continue;
                        append: stringBuilder.AppendLine(line_value);
                    }
                }

                if (isUpdateOrRemove)
                {
                    foreach (var item in hosts)
                    {
                        insert_mark_datas.TryAdd(item.Key, item.Value);
                    }
                }

                void Restore(IEnumerable<KeyValuePair<string, (int line_num, string line_value)>> datas)
                {
                    foreach (var item in datas)
                    {
                        var line_index = stringBuilder.GetLineIndex(item.Value.line_num);
                        var line_value = item.Value.line_value;
                        if (line_index >= 0)
                        {
                            stringBuilder.Insert(line_index, $"{line_value}{Environment.NewLine}");
                        }
                        else
                        {
                            stringBuilder.AppendLine(line_value);
                        }
                    }
                }

                var is_restore = !has_hosts && !isUpdateOrRemove;
                if (is_restore)
                {
                    Restore(backup_datas);
                }
                else
                {
                    var any_insert_mark_datas = insert_mark_datas.Any();

                    var restore_backup_datas = any_insert_mark_datas ? backup_datas.Where(x => !insert_mark_datas.ContainsKey(x.Key)) : backup_datas;
                    Restore(restore_backup_datas); // 恢复备份数据

                    if (any_insert_mark_datas) // 插入新增数据
                    {
                        stringBuilder.AppendLine();
                        stringBuilder.AppendLine(MarkStart);
                        foreach (var item in insert_mark_datas)
                        {
                            stringBuilder.AppendFormat("{0} {1}", item.Value, item.Key);
                            stringBuilder.AppendLine();
                        }
                        stringBuilder.AppendLine(MarkEnd);
                    }

                    var any_backup_insert_mark_datas = any_insert_mark_datas && backup_insert_mark_datas.Any();
                    var insert_backup_datas = any_insert_mark_datas ? backup_datas.Where(x => insert_mark_datas.ContainsKey(x.Key)) : backup_datas;
                    if (any_backup_insert_mark_datas) insert_backup_datas = insert_backup_datas.Where(x => !backup_insert_mark_datas.ContainsKey(x.Key));
                    if (any_backup_insert_mark_datas || insert_backup_datas.Any()) // 插入备份数据
                    {
                        stringBuilder.AppendLine();
                        stringBuilder.AppendLine(BackupMarkStart);
                        if (any_backup_insert_mark_datas)
                        {
                            foreach (var item in backup_insert_mark_datas) // #{line_num} {line_value}
                            {
                                stringBuilder.Append('#');
                                stringBuilder.Append(item.Value.line_num);
                                stringBuilder.Append(' ');
                                stringBuilder.AppendLine(item.Value.line_value);
                            }
                        }
                        foreach (var item in insert_backup_datas)
                        {
                            stringBuilder.AppendLine(item.Value.line_value);
                        }
                        stringBuilder.AppendLine(BackupMarkEnd);
                    }
                }

                var contents = stringBuilder.ToString();
                File.WriteAllText(fileInfo.FullName, contents);

                result.ResultType = OperationResultType.Success;
                result.Message = AppResources.Hosts_UpdateSuccess;
            }
            catch (Exception ex)
            {
                Log.Error(TAG, ex, "UpdateHosts catch.");
                result.ResultType = OperationResultType.Error;
                result.AppendData = ex;
                result.Message = ex.Message;
                return result;
            }

            if (removeReadOnly) SetReadOnly(fileInfo);
            return result;
        }

        #endregion

        public void OpenFile() => s.OpenFileByTextReader(s.HostsFilePath);

        public OperationResult<List<(string ip, string domain)>> ReadHostsAllLines()
        {
            static IEnumerable<(string ip, string domain)> ReadHostsAllLines(StreamReader fileReader)
            {
                int index = 0;
                HashSet<string> list = new();
                while (true)
                {
                    index++;
                    var line = fileReader.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!HandleLine(index, list, line, out var array)) continue;
                    yield return (array[0], array[1]);
                }
            }
            var result = new OperationResult<List<(string ip, string domain)>>(OperationResultType.Error, AppResources.Hosts_ReadError);
            if (!TryOperation(out var errmsg, out var fileInfo, out var _))
            {
                result.Message = errmsg;
                return result;
            }
            try
            {
                using var fileReader = fileInfo.OpenText();
                result.AppendData.AddRange(ReadHostsAllLines(fileReader));
                result.ResultType = OperationResultType.Success;
                result.Message = AppResources.Hosts_ReadSuccess;
            }
            catch (Exception ex)
            {
                Log.Error(TAG, ex, "ReadHostsAllLines catch.");
                result.ResultType = OperationResultType.Error;
                result.Message = ex.Message;
            }
            return result;
        }

        public OperationResult UpdateHosts(string ip, string domain)
        {
            var dict = new Dictionary<string, string>
            {
                { domain, ip },
            };
            return UpdateHosts(dict);
        }

        public OperationResult UpdateHosts(IEnumerable<(string ip, string domain)> hosts)
        {
            var value = hosts.ToDictionary(k => k.domain, v => v.ip);
            return UpdateHosts(value);
        }

        public OperationResult UpdateHosts(IReadOnlyDictionary<string, string> hosts)
        {
            return HandleHosts(isUpdateOrRemove: true, hosts);
        }

        public OperationResult RemoveHosts(string ip, string domain)
        {
            var hosts = new Dictionary<string, string>
            {
                { domain, ip },
            };
            return HandleHosts(isUpdateOrRemove: false, hosts);
        }

        public OperationResult RemoveHosts(string domain) => RemoveHosts(string.Empty, domain);

        public OperationResult RemoveHostsByTag()
        {
            return HandleHosts(isUpdateOrRemove: false);
        }
    }
}