// Copyright (c) 2021 The Trade Desk, Inc
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

using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UID2.Client.Utils;

namespace UID2.Client
{
    internal static class UID2Encryption
    {
        public const int GCM_AUTHTAG_LENGTH = 16;
        public const int GCM_IV_LENGTH = 12;

        internal static DecryptionResponse Decrypt(byte[] encryptedId, IKeyContainer keys, DateTime now, IdentityScope identityScope)
        {
            if (encryptedId[0] == 2)
            {
                return DecryptV2(encryptedId, keys, now);
            }
            else if (encryptedId[1] == 112)
            {
                return DecryptV3(encryptedId, keys, now, identityScope);
            }

            return DecryptionResponse.MakeError(DecryptionStatus.VersionNotSupported);
        }

        private static DecryptionResponse DecryptV2(byte[] encryptedId, IKeyContainer keys, DateTime now)
        {
            var reader = new BigEndianByteReader(new MemoryStream(encryptedId));

            // version
            reader.ReadByte();

            var masterKeyId = reader.ReadInt32();

            Key masterKey = null;
            if (!keys.TryGetKey(masterKeyId, out masterKey))
            {
                return DecryptionResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            var masterDecrypted = Decrypt(new ByteArraySlice(encryptedId, 21, encryptedId.Length - 21), reader.ReadBytes(16), masterKey.Secret);

            var masterPayloadReader = new BigEndianByteReader(new MemoryStream(masterDecrypted));

            long expiresMilliseconds = masterPayloadReader.ReadInt64();

            var siteKeyId = masterPayloadReader.ReadInt32();

            Key siteKey = null;
            if (!keys.TryGetKey(siteKeyId, out siteKey))
            {
                return DecryptionResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            var identityDecrypted =
                Decrypt(new ByteArraySlice(masterDecrypted, 28, masterDecrypted.Length - 28),
                    masterPayloadReader.ReadBytes(16), siteKey.Secret);

            var identityPayloadReader = new BigEndianByteReader(new MemoryStream(identityDecrypted));

            var siteId = identityPayloadReader.ReadInt32();
            var idLength = identityPayloadReader.ReadInt32();

            var idString = Encoding.UTF8.GetString(identityPayloadReader.ReadBytes(idLength));

            var privacyBits = identityPayloadReader.ReadInt32();

            var establishedMilliseconds = identityPayloadReader.ReadInt64();

            var established = DateTimeUtils.FromEpochMilliseconds(establishedMilliseconds);

            var expiry = DateTimeUtils.FromEpochMilliseconds(expiresMilliseconds);
            if (expiry < now)
            {
                return new DecryptionResponse(DecryptionStatus.ExpiredToken, null, established, siteId, siteKey.SiteId);
            }
            else
            {
                return new DecryptionResponse(DecryptionStatus.Success, idString, established, siteId, siteKey.SiteId);
            }
        }

        private static DecryptionResponse DecryptV3(byte[] encryptedId, IKeyContainer keys, DateTime now, IdentityScope identityScope)
        {
            var reader = new BigEndianByteReader(new MemoryStream(encryptedId));

            var prefix = reader.ReadByte();
            if (DecodeIdentityScopeV3(prefix) != identityScope)
            {
                return DecryptionResponse.MakeError(DecryptionStatus.InvalidIdentityScope);
            }

            // version
            reader.ReadByte();

            var masterKeyId = reader.ReadInt32();

            Key masterKey = null;
            if (!keys.TryGetKey(masterKeyId, out masterKey))
            {
                return DecryptionResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            var masterDecrypted = DecryptGCM(new ByteArraySlice(encryptedId, 6, encryptedId.Length - 6), masterKey.Secret);
            var masterPayloadReader = new BigEndianByteReader(new MemoryStream(masterDecrypted));

            long expiresMilliseconds = masterPayloadReader.ReadInt64();
            long createdMilliseconds = masterPayloadReader.ReadInt64();

            int operatorSiteId = masterPayloadReader.ReadInt32();
            byte operatorType = masterPayloadReader.ReadByte();
            int operatorVersion = masterPayloadReader.ReadInt32();
            int operatorKeyId = masterPayloadReader.ReadInt32();

            var siteKeyId = masterPayloadReader.ReadInt32();

            Key siteKey = null;
            if (!keys.TryGetKey(siteKeyId, out siteKey))
            {
                return DecryptionResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            var sitePayload = DecryptGCM(new ByteArraySlice(masterDecrypted, 33, masterDecrypted.Length - 33), siteKey.Secret);
            var sitePayloadReader = new BigEndianByteReader(new MemoryStream(sitePayload));

            var siteId = sitePayloadReader.ReadInt32();
            var publisherId = sitePayloadReader.ReadInt64();
            var publisherKeyId = sitePayloadReader.ReadInt32();

            var privacyBits = sitePayloadReader.ReadInt32();
            var establishedMilliseconds = sitePayloadReader.ReadInt64();
            var refreshedMilliseconds = sitePayloadReader.ReadInt64();
            var id = sitePayloadReader.ReadBytes(sitePayload.Length - 36);

            var established = DateTimeUtils.FromEpochMilliseconds(establishedMilliseconds);
            var idString = Convert.ToBase64String(id);

            var expiry = DateTimeUtils.FromEpochMilliseconds(expiresMilliseconds);
            if (expiry < now)
            {
                return new DecryptionResponse(DecryptionStatus.ExpiredToken, null, established, siteId, siteKey.SiteId);
            }
            else
            {
                return new DecryptionResponse(DecryptionStatus.Success, idString, established, siteId, siteKey.SiteId);
            }
        }

        internal static EncryptionDataResponse EncryptData(EncryptionDataRequest request, IKeyContainer keys, IdentityScope identityScope)
        {
            if (request.Data == null)
            {
                throw new ArgumentNullException("data");
            }

            DateTime now = request.Now;
            Key key = request.Key;
            int siteId = -1;
            if (key == null)
            {
                int siteKeySiteId = -1;
                if (keys == null)
                {
                    return EncryptionDataResponse.MakeError(EncryptionStatus.NotInitialized);
                }
                else if (!keys.IsValid(now))
                {
                    return EncryptionDataResponse.MakeError(EncryptionStatus.KeysNotSynced);
                }
                else if (request.SiteId.HasValue && request.AdvertisingToken != null)
                {
                    throw new ArgumentException("only one of siteId or advertisingToken can be specified");
                }
                else if (request.SiteId.HasValue)
                {
                    siteId = request.SiteId.Value;
                    siteKeySiteId = siteId;
                }
                else
                {
                    try
                    {
                        DecryptionResponse decryptedToken = Decrypt(Convert.FromBase64String(request.AdvertisingToken), keys, now, identityScope);
                        if (!decryptedToken.Success)
                        {
                            return EncryptionDataResponse.MakeError(EncryptionStatus.TokenDecryptFailure);
                        }

                        siteId = decryptedToken.SiteId.Value;
                        siteKeySiteId = decryptedToken.SiteKeySiteId.Value;
                    }
                    catch (Exception)
                    {
                        return EncryptionDataResponse.MakeError(EncryptionStatus.TokenDecryptFailure);
                    }
                }

                if (!keys.TryGetActiveSiteKey(siteKeySiteId, now, out key))
                {
                    return EncryptionDataResponse.MakeError(EncryptionStatus.NotAuthorizedForKey);
                }
            }
            else if (!key.IsActive(now))
            {
                return EncryptionDataResponse.MakeError(EncryptionStatus.KeyInactive);
            }
            else
            {
                siteId = key.SiteId;
            }

            byte[] iv = request.InitializationVector;
            if (iv == null)
            {
                iv = GenerateIV(GCM_IV_LENGTH);
            }

            try
            {
                var payloadStream = new MemoryStream(request.Data.Length + 12);
                var payloadWriter = new BigEndianByteWriter(payloadStream);
                payloadWriter.Write(DateTimeUtils.DateTimeToEpochMilliseconds(now));
                payloadWriter.Write(siteId);
                payloadWriter.Write(request.Data);

                byte[] encryptedData = EncryptGCM(payloadStream.ToArray(), iv, key.Secret);
                var ms = new MemoryStream(encryptedData.Length + GCM_IV_LENGTH + GCM_AUTHTAG_LENGTH + 6);
                var writer = new BigEndianByteWriter(ms);
                writer.Write((byte)((int)PayloadType.ENCRYPTED_DATA_V3 | ((int)identityScope << 4) | 0xB));
                writer.Write((byte)112); // version
                writer.Write((int)key.Id);
                writer.Write(iv);
                writer.Write(encryptedData);
                return EncryptionDataResponse.MakeSuccess(Convert.ToBase64String(ms.ToArray()));
            }
            catch (Exception)
            {
                return EncryptionDataResponse.MakeError(EncryptionStatus.EncryptionFailure);
            }
        }

        internal static DecryptionDataResponse DecryptData(byte[] encryptedBytes, IKeyContainer keys, IdentityScope identityScope)
        {
            if ((encryptedBytes[0] & 224) == (int)PayloadType.ENCRYPTED_DATA_V3)
            {
                return DecryptDataV3(encryptedBytes, keys, identityScope);
            }
            else
            {
                return DecryptDataV2(encryptedBytes, keys);
            }
        }

        internal static DecryptionDataResponse DecryptDataV2(byte[] encryptedBytes, IKeyContainer keys)
        {
            var reader = new BigEndianByteReader(new MemoryStream(encryptedBytes));
            if (reader.ReadByte() != (byte)PayloadType.ENCRYPTED_DATA)
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.InvalidPayloadType);
            }
            if (reader.ReadByte() != 1)
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.VersionNotSupported);
            }

            DateTime encryptedAt = DateTimeUtils.FromEpochMilliseconds(reader.ReadInt64());
            int siteId = reader.ReadInt32();
            long keyId = reader.ReadInt32();

            if (!keys.TryGetKey(keyId, out var key))
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            byte[] iv = reader.ReadBytes(16);
            byte[] decryptedData = Decrypt(new ByteArraySlice(encryptedBytes, 34, encryptedBytes.Length - 34), iv, key.Secret);

            return DecryptionDataResponse.MakeSuccess(decryptedData, encryptedAt);
        }

        internal static DecryptionDataResponse DecryptDataV3(byte[] encryptedBytes, IKeyContainer keys, IdentityScope identityScope)
        {
            var reader = new BigEndianByteReader(new MemoryStream(encryptedBytes));
            var payloadScope = DecodeIdentityScopeV3(reader.ReadByte());
            if (payloadScope != identityScope)
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.InvalidIdentityScope);
            }
            if (reader.ReadByte() != 112)
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.VersionNotSupported);
            }

            long keyId = reader.ReadInt32();
            if (!keys.TryGetKey(keyId, out var key))
            {
                return DecryptionDataResponse.MakeError(DecryptionStatus.NotAuthorizedForKey);
            }

            var decryptedBytes = DecryptGCM(new ByteArraySlice(encryptedBytes, 6, encryptedBytes.Length - 6), key.Secret);
            var decryptedReader = new BigEndianByteReader(new MemoryStream(decryptedBytes));

            DateTime encryptedAt = DateTimeUtils.FromEpochMilliseconds(decryptedReader.ReadInt64());
            int siteId = decryptedReader.ReadInt32();

            var decryptedData = new byte[decryptedBytes.Length - 12];
            Array.Copy(decryptedBytes, 12, decryptedData, 0, decryptedData.Length);

            return DecryptionDataResponse.MakeSuccess(decryptedData, encryptedAt);
        }

        private static byte[] Decrypt(ByteArraySlice arraySlice, byte[] iv, byte[] secret)
        {
            using (var r = new RijndaelManaged() { Key = secret, IV = iv, Mode = CipherMode.CBC })
            using (var m = new MemoryStream(arraySlice.Buffer, arraySlice.Offset, arraySlice.Count))
            using (var cs = new CryptoStream(m, r.CreateDecryptor(), CryptoStreamMode.Read))
            using (var ms = new MemoryStream())
            {
                cs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static byte[] Encrypt(byte[] data, byte[] iv, byte[] secret)
        {
            using (var r = new RijndaelManaged() { Key = secret, IV = iv, Mode = CipherMode.CBC })
            using (var m = new MemoryStream(data))
            using (var cs = new CryptoStream(m, r.CreateEncryptor(), CryptoStreamMode.Read))
            using (var ms = new MemoryStream())
            {
                ms.Write(iv, 0, 16);
                cs.CopyTo(ms);

                return ms.ToArray();
            }
        }

        internal static (byte[], byte[]) EncryptGCM(byte[] data, byte[] secret)
        {
            var iv = GenerateIV(GCM_IV_LENGTH);
            return (iv, EncryptGCM(data, iv, secret));
        }

        internal static byte[] EncryptGCM(byte[] data, byte[] iv, byte[] secret)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(secret), GCM_AUTHTAG_LENGTH * 8, iv, null);
            cipher.Init(true, parameters);
            var cipherText = new byte[cipher.GetOutputSize(data.Length)];
            var len = cipher.ProcessBytes(data, 0, data.Length, cipherText, 0);
            cipher.DoFinal(cipherText, len);
            return cipherText;
        }

        internal static byte[] DecryptGCM(ByteArraySlice cipherText, byte[] secret)
        {
            var iv = new byte[GCM_IV_LENGTH];
            Array.Copy(cipherText.Buffer, cipherText.Offset, iv, 0, iv.Length);
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(secret), GCM_AUTHTAG_LENGTH * 8, iv, null);
            cipher.Init(false, parameters);
            var plainText = new byte[cipher.GetOutputSize(cipherText.Count - GCM_IV_LENGTH)];
            var len = cipher.ProcessBytes(cipherText.Buffer, cipherText.Offset + GCM_IV_LENGTH, cipherText.Count - GCM_IV_LENGTH, plainText, 0);
            cipher.DoFinal(plainText, len);
            return plainText;
        }

        private static byte[] GenerateIV(int len = 16)
        {
            byte[] iv = new byte[len];
            RNGCryptoServiceProvider.Create().GetBytes(iv);
            return iv;
        }

        private static IdentityScope DecodeIdentityScopeV3(byte value)
        {
            return (IdentityScope)((value >> 4) & 1);
        }
    }
}