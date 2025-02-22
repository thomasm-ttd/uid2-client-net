﻿// Copyright (c) 2021 The Trade Desk, Inc
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice,
//    this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.

using System;
using System.Collections.Generic;
using System.Text;

namespace UID2.Client
{
    public struct DecryptionResponse
    {
        private readonly DecryptionStatus _status;
        private readonly string _uid;
        private readonly DateTime? _established;
        private readonly int? _siteId;
        private readonly int? _siteKeySiteId;

        public DecryptionResponse(DecryptionStatus status, string uid, DateTime? established, int? siteId, int? siteKeySiteId)
        {
            _status = status;
            _uid = uid;
            _established = established;
            _siteId = siteId;
            _siteKeySiteId = siteKeySiteId;
        }

        public static DecryptionResponse MakeError(DecryptionStatus status)
        {
            return new DecryptionResponse(status, null, null, null, null);
        }

        public bool Success => _status == DecryptionStatus.Success;
        public DecryptionStatus Status => _status;
        public string Uid => _uid;
        public DateTime? Established => _established;
        public int? SiteId => _siteId;
        public int? SiteKeySiteId => _siteKeySiteId;
    }
}
