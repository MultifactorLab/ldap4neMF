﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LdapForNet.Utils;
using static LdapForNet.Native.Native;

namespace LdapForNet
{
    public partial class LdapConnection: ILdapConnection
    {
        public void Connect(string hostname, int port = (int)LdapPort.LDAP, LdapVersion version = LdapVersion.LDAP_VERSION3)
        {
            var details = new Dictionary<string, string>
            {
                [nameof(hostname)]=hostname,
                [nameof(port)] = port.ToString(),
                [nameof(version)] = version.ToString()
            };
            var nativeHandle = IntPtr.Zero;
            ThrowIfError(
                ldap_initialize(ref nativeHandle, $"LDAP://{hostname}:{port}"),
                nameof(ldap_initialize),
                details
            );
            _ld = new LdapHandle(nativeHandle);
            var ldapVersion = (int)version;

            ThrowIfError(
                ldap_set_option(_ld, (int)LdapOption.LDAP_OPT_PROTOCOL_VERSION, ref ldapVersion),
                nameof(ldap_set_option),
                details
            );
        }

        public void Bind(string mechanism = LdapAuthMechanism.GSSAPI, string userDn = null, string password = null)
        {
            ThrowIfNotInitialized();
            if (LdapAuthMechanism.SIMPLE.Equals(mechanism,StringComparison.OrdinalIgnoreCase))
            {
                SimpleBind(userDn,password);
            }
            else if (LdapAuthMechanism.GSSAPI.Equals(mechanism,StringComparison.OrdinalIgnoreCase))
            {
                GssApiBind();
            }
            else
            {
                throw new LdapException($"Not implemented mechanism: {mechanism}. Available: {LdapAuthMechanism.GSSAPI} | {LdapAuthMechanism.SIMPLE}. ");
            }

            _bound = true;
        }

        public async Task BindAsync(string mechanism = LdapAuthMechanism.GSSAPI, string userDn = null, string password = null)
        {
            ThrowIfNotInitialized();
            IntPtr result;
            if (LdapAuthMechanism.SIMPLE.Equals(mechanism,StringComparison.OrdinalIgnoreCase))
            {
                result = await SimpleBindAsync(userDn,password);
            }
            else if (LdapAuthMechanism.GSSAPI.Equals(mechanism,StringComparison.OrdinalIgnoreCase))
            {
                result = await GssApiBindAsync();
            }
            else
            {
                throw new LdapException($"Not implemented mechanism: {mechanism}. Available: {LdapAuthMechanism.GSSAPI} | {LdapAuthMechanism.SIMPLE}. ");
            }

            if (result != IntPtr.Zero)
            {
                ThrowIfParseResultError(result);
            }
            
            _bound = true;
        }

        public void SetOption(LdapOption option, int value)
        {
            ThrowIfNotBound();
            ThrowIfError(ldap_set_option(_ld, (int)option, ref value),nameof(ldap_set_option));
        }
        
        public void SetOption(LdapOption option, string value)
        {
            ThrowIfNotBound();
            ThrowIfError(ldap_set_option(_ld, (int)option, ref value),nameof(ldap_set_option));
        }
        
        public void SetOption(LdapOption option, IntPtr valuePtr)
        {
            ThrowIfNotBound();
            ThrowIfError(ldap_set_option(_ld, (int)option, valuePtr),nameof(ldap_set_option));
        }

        public IList<LdapEntry> Search(string @base, string filter,
            LdapSearchScope scope = LdapSearchScope.LDAP_SCOPE_SUBTREE)
        {
            var response = (SearchResponse)SendRequest(new SearchRequest(@base, filter, scope));
            return response.Entries;
        }
        
        public async Task<IList<LdapEntry>> SearchAsync(string @base, string filter,
            LdapSearchScope scope = LdapSearchScope.LDAP_SCOPE_SUBTREE)
        {
            var response = (SearchResponse)await SendRequestAsync(new SearchRequest(@base, filter, scope));
            return response.Entries;
        }

        
        public void Add(LdapEntry entry)
        {
            ThrowIfNotBound();
            if (string.IsNullOrWhiteSpace(entry.Dn))
            {
                throw new ArgumentNullException(nameof(entry.Dn));
            }

            if (entry.Attributes == null)
            {
                entry.Attributes = new Dictionary<string, List<string>>();
            }

            var attrs = entry.Attributes.Select(ToLdapMod).ToList();
            
            var ptr = Marshal.AllocHGlobal(IntPtr.Size*(attrs.Count+1)); // alloc memory for list with last element null
            MarshalUtils.StructureArrayToPtr(attrs,ptr, true);

            try
            {
                ThrowIfError(_ld, ldap_add_ext_s(_ld,
                    entry.Dn,
                    ptr,                
                    IntPtr.Zero, 
                    IntPtr.Zero 
                ), nameof(ldap_add_ext_s));

            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
                attrs.ForEach(_ => { Marshal.FreeHGlobal(_.mod_vals_u.modv_strvals); });
            }
        }

        public async Task<DirectoryResponse> SendRequestAsync(DirectoryRequest directoryRequest, CancellationToken token = default)
        {
            if (token.IsCancellationRequested)
            {
                return default;
            }

            ThrowIfNotBound();
            
            var requestHandler = SendRequest(directoryRequest, out var messageId);

            return await Task.Factory.StartNew(() => ProcessResponse(directoryRequest, requestHandler, messageId, token), token).ConfigureAwait(false);
        }

        public DirectoryResponse SendRequest(DirectoryRequest directoryRequest)
        {
            ThrowIfNotBound();
            var requestHandler = SendRequest(directoryRequest, out var messageId);
            return ProcessResponse(directoryRequest, requestHandler, messageId, CancellationToken.None);
        }

        private DirectoryResponse ProcessResponse(DirectoryRequest directoryRequest,
            IRequestHandler requestHandler, int messageId,
            CancellationToken token)
        {
            var status = LdapResultCompleteStatus.Unknown;
            var msg = Marshal.AllocHGlobal(IntPtr.Size);

            DirectoryResponse response = default;

            while (status != LdapResultCompleteStatus.Complete && !token.IsCancellationRequested)
            {
                var resType = ldap_result(_ld, messageId, 0, IntPtr.Zero, ref msg);
                ThrowIfResultError(directoryRequest, resType);

                status = requestHandler.Handle(_ld, resType, msg, out response);

                if (status == LdapResultCompleteStatus.Unknown)
                {
                    throw new LdapException($"Unknown search type {resType}", nameof(ldap_result), 1);
                }

                if (status == LdapResultCompleteStatus.Complete)
                {
                    ThrowIfParseResultError(msg);
                }
            }

            return response;
        }

        private IRequestHandler SendRequest(DirectoryRequest directoryRequest, out int messageId)
        {
            var operation = GetLdapOperation(directoryRequest);
            var requestHandler = GetSendRequestHandler(operation);
            messageId = 0;
            ThrowIfError(_ld, requestHandler.SendRequest(_ld, directoryRequest, ref messageId), requestHandler.GetType().Name);
            return requestHandler;
        }

        private static void ThrowIfResultError(DirectoryRequest directoryRequest, LdapResultType resType)
        {
            switch (resType)
            {
                case LdapResultType.LDAP_ERROR:
                    ThrowIfError(1, directoryRequest.GetType().Name);
                    break;
                case LdapResultType.LDAP_TIMEOUT:
                    throw new LdapException("Timeout exceeded", nameof(ldap_result), 1);
            }
        }

        private IRequestHandler GetSendRequestHandler(LdapOperation operation)
        {
            switch (operation)
            {
                case LdapOperation.LdapAdd:
                    return new AddRequestHandler();
                    break;
                case LdapOperation.LdapModify:
                    break;
                case LdapOperation.LdapSearch:
                    return new SearchRequestHandler();
                    break;
                case LdapOperation.LdapDelete:
                    break;
                case LdapOperation.LdapModifyDn:
                    break;
                case LdapOperation.LdapCompare:
                    break;
                case LdapOperation.LdapExtendedRequest:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        public async Task AddAsync(LdapEntry entry, CancellationToken token = default) => await SendRequestAsync(new AddRequest(entry), token);

        private void ThrowIfParseResultError(IntPtr msg)
        {
            var matchedMessage = string.Empty;
            var errorMessage = string.Empty;
            var res = 0;
            var referrals = IntPtr.Zero;
            var serverctrls = IntPtr.Zero;
            ThrowIfError(_ld, ldap_parse_result(_ld, msg, ref res, ref matchedMessage, ref errorMessage,
                ref referrals, ref serverctrls, 1), nameof(ldap_parse_result));
            ThrowIfError(_ld, res, nameof(ldap_parse_result), new Dictionary<string, string>
            {
                [nameof(errorMessage)] = errorMessage,
                [nameof(matchedMessage)] = matchedMessage
            });
        }

        public void Modify(LdapModifyEntry entry)
        {
            ThrowIfNotBound();
            
            if (string.IsNullOrWhiteSpace(entry.Dn))
            {
                throw new ArgumentNullException(nameof(entry.Dn));
            }
            
            if (entry.Attributes == null)
            {
                entry.Attributes = new List<LdapModifyAttribute>();
            }
            
            var attrs = entry.Attributes.Select(ToLdapMod).ToList();
            
            var ptr = Marshal.AllocHGlobal(IntPtr.Size*(attrs.Count+1)); // alloc memory for list with last element null
            MarshalUtils.StructureArrayToPtr(attrs,ptr, true);

            try
            {
                ThrowIfError(_ld, ldap_modify_ext_s(_ld,
                    entry.Dn,
                    ptr,                
                    IntPtr.Zero, 
                    IntPtr.Zero 
                ), nameof(ldap_modify_ext_s));

            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
                attrs.ForEach(_ => { Marshal.FreeHGlobal(_.mod_vals_u.modv_strvals); });
            }
        }

        public void Dispose()
        {
            _ld?.Dispose();
        }

        public IntPtr GetNativeLdapPtr()
        {
            return _ld.DangerousGetHandle();
        }


        public void Delete(string dn)
        {
            ThrowIfNotBound();
            if (string.IsNullOrWhiteSpace(dn))
            {
                throw new ArgumentNullException(nameof(dn));
            }
            ThrowIfError(_ld, ldap_delete_ext_s(_ld,
                dn,
                IntPtr.Zero, 
                IntPtr.Zero 
            ), nameof(ldap_delete_ext_s));
        }

        public void Rename(string dn, string newRdn, string newParent, bool isDeleteOldRdn)
        {
            ThrowIfNotBound();
            if (dn == null)
            {
                throw new ArgumentNullException(nameof(dn));
            }
            ThrowIfError(_ld, ldap_rename_s(_ld,
                dn,
                newRdn,
                newParent,
                isDeleteOldRdn?1:0,
                IntPtr.Zero, 
                IntPtr.Zero 
            ), nameof(ldap_rename_s));
        }
    }
    
    internal enum LdapOperation
    {
        LdapAdd = 0,
        LdapModify = 1,
        LdapSearch = 2,
        LdapDelete = 3,
        LdapModifyDn = 4,
        LdapCompare = 5,
        LdapExtendedRequest = 6
    }
}