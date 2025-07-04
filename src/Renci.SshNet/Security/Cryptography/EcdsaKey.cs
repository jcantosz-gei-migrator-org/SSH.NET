#nullable enable
using System;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using Renci.SshNet.Common;
using Renci.SshNet.Security.Cryptography;

namespace Renci.SshNet.Security
{
    /// <summary>
    /// Contains ECDSA (ecdsa-sha2-nistp{256,384,521}) private and public key.
    /// </summary>
    public partial class EcdsaKey : Key, IDisposable
    {
#pragma warning disable SA1310 // Field names should not contain underscore
        private const string ECDSA_P256_OID_VALUE = "1.2.840.10045.3.1.7"; // Also called nistP256 or secP256r1
        private const string ECDSA_P384_OID_VALUE = "1.3.132.0.34"; // Also called nistP384 or secP384r1
        private const string ECDSA_P521_OID_VALUE = "1.3.132.0.35"; // Also called nistP521or secP521r1
#pragma warning restore SA1310 // Field names should not contain underscore

        private static readonly BigInteger Encoded256 = "nistp256"u8.ToBigInteger();
        private static readonly BigInteger Encoded384 = "nistp384"u8.ToBigInteger();
        private static readonly BigInteger Encoded521 = "nistp521"u8.ToBigInteger();

        private EcdsaDigitalSignature? _digitalSignature;

#pragma warning disable SA1401 // Fields should be private; internal readonly
        internal readonly Impl _impl;
#pragma warning restore SA1401 // Fields should be private

        /// <summary>
        /// Gets the SSH name of the ECDSA Key.
        /// </summary>
        /// <returns>
        /// The SSH name of the ECDSA Key.
        /// </returns>
        public override string ToString()
        {
            return string.Format("ecdsa-sha2-nistp{0}", KeyLength);
        }

        /// <summary>
        /// Gets the HashAlgorithm to use.
        /// </summary>
        public HashAlgorithmName HashAlgorithm
        {
            get
            {
                switch (KeyLength)
                {
                    case 256:
                        return HashAlgorithmName.SHA256;
                    case 384:
                        return HashAlgorithmName.SHA384;
                    case 521:
                        return HashAlgorithmName.SHA512;
                    default:
                        return HashAlgorithmName.SHA256;
                }
            }
        }

        internal abstract class Impl : IDisposable
        {
            public abstract int KeyLength { get; }

            public abstract byte[]? PrivateKey { get; }

#if NET
            public abstract ECDsa Ecdsa { get; }
#else
            public abstract ECDsa? Ecdsa { get; }
#endif

            public abstract bool Verify(byte[] input, byte[] signature);

            public abstract byte[] Sign(byte[] input);

            public abstract void Export(out byte[] qx, out byte[] qy);

            protected abstract void Dispose(bool disposing);

            public void Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        /// <inheritdoc/>
        public override int KeyLength
        {
            get
            {
                return _impl.KeyLength;
            }
        }

        /// <inheritdoc/>
        protected internal override DigitalSignature DigitalSignature
        {
            get
            {
                _digitalSignature ??= new EcdsaDigitalSignature(this);

                return _digitalSignature;
            }
        }

        /// <summary>
        /// Gets the ECDSA public key.
        /// </summary>
        /// <value>
        /// An array with the ASCII-encoded curve identifier (e.g. "nistp256")
        /// at index 0, and the public curve point Q at index 1.
        /// </value>
        public override BigInteger[] Public
        {
            get
            {
                BigInteger curve;
                switch (KeyLength)
                {
                    case 256:
                        curve = Encoded256;
                        break;
                    case 384:
                        curve = Encoded384;
                        break;
                    default:
                        Debug.Assert(KeyLength == 521);
                        curve = Encoded521;
                        break;
                }

                _impl.Export(out var qx, out var qy);

                // Make ECPoint from x and y
                // Prepend 04 (uncompressed format) + qx-bytes + qy-bytes
                var q = new byte[1 + qx.Length + qy.Length];
                q[0] = 0x4;
                Buffer.BlockCopy(qx, 0, q, 1, qx.Length);
                Buffer.BlockCopy(qy, 0, q, qx.Length + 1, qy.Length);

                // returns Curve-Name and x/y as ECPoint
#if NETSTANDARD2_1 || NET
                return new[] { curve, new BigInteger(q, isBigEndian: true) };
#else
                Array.Reverse(q);
                return new[] { curve, new BigInteger(q) };
#endif
            }
        }

        /// <summary>
        /// Gets the PrivateKey Bytes.
        /// </summary>
        public byte[]? PrivateKey
        {
            get
            {
                return _impl.PrivateKey;
            }
        }

        /// <summary>
        /// Gets the <see cref="ECDsa"/> object.
        /// </summary>
#if NET
        public ECDsa Ecdsa
#else
        public ECDsa? Ecdsa
#endif
        {
            get
            {
                return _impl.Ecdsa;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EcdsaKey"/> class.
        /// </summary>
        /// <param name="publicKeyData">The encoded public key data.</param>
        public EcdsaKey(SshKeyData publicKeyData)
        {
            ThrowHelper.ThrowIfNull(publicKeyData);

            if (!publicKeyData.Name.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) || publicKeyData.Keys.Length != 2)
            {
                throw new ArgumentException($"Invalid ECDSA public key data. ({publicKeyData.Name}, {publicKeyData.Keys.Length}).", nameof(publicKeyData));
            }

            var curve_s = Encoding.ASCII.GetString(publicKeyData.Keys[0].ToByteArray(isBigEndian: true));
            var curve_oid = GetCurveOid(curve_s);

            var publickey = publicKeyData.Keys[1].ToByteArray(isBigEndian: true);
            _impl = Import(curve_oid, publickey, privatekey: null);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EcdsaKey"/> class.
        /// </summary>
        /// <param name="curve">The curve name or oid.</param>
        /// <param name="publickey">Value of publickey.</param>
        /// <param name="privatekey">Value of privatekey.</param>
        public EcdsaKey(string curve, byte[] publickey, byte[] privatekey)
        {
            _impl = Import(GetCurveOid(curve), publickey, privatekey);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EcdsaKey"/> class.
        /// </summary>
        /// <param name="data">DER encoded private key data.</param>
        public EcdsaKey(byte[] data)
        {
            var keyReader = new AsnReader(data, AsnEncodingRules.DER);
            var sequenceReader = keyReader.ReadSequence();
            keyReader.ThrowIfNotEmpty();

            _ = sequenceReader.ReadInteger(); // skip version

            var privatekey = sequenceReader.ReadOctetString().TrimLeadingZeros();

            var s0 = sequenceReader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            var curve = s0.ReadObjectIdentifier();

            var s1 = sequenceReader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true));
            var pubkey = s1.ReadBitString(out _);

            sequenceReader.ThrowIfNotEmpty();

            _impl = Import(curve, pubkey, privatekey);
        }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
        private static Impl Import(string curve_oid, byte[] publickey, byte[]? privatekey)
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
        {
            // ECPoint as BigInteger(2)
            var cord_size = (publickey.Length - 1) / 2;
            var qx = new byte[cord_size];
            Buffer.BlockCopy(publickey, 1, qx, 0, qx.Length);

            var qy = new byte[cord_size];
            Buffer.BlockCopy(publickey, cord_size + 1, qy, 0, qy.Length);

#if NET
            return new BclImpl(curve_oid, cord_size, qx, qy, privatekey);
#else
            try
            {
#if NET462
                return new CngImpl(curve_oid, cord_size, qx, qy, privatekey);
#else
                return new BclImpl(curve_oid, cord_size, qx, qy, privatekey);
#endif
            }
            catch (NotImplementedException)
            {
                // Mono doesn't implement ECDsa.Create()
                // See https://github.com/mono/mono/blob/main/mcs/class/referencesource/System.Core/System/Security/Cryptography/ECDsa.cs#L32
                return new BouncyCastleImpl(curve_oid, qx, qy, privatekey);
            }
#endif
        }

        private static string GetCurveOid(string curve)
        {
            if (string.Equals(curve, "nistp256", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(curve, ECDSA_P256_OID_VALUE))
            {
                return ECDSA_P256_OID_VALUE;
            }

            if (string.Equals(curve, "nistp384", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(curve, ECDSA_P384_OID_VALUE))
            {
                return ECDSA_P384_OID_VALUE;
            }

            if (string.Equals(curve, "nistp521", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(curve, ECDSA_P521_OID_VALUE))
            {
                return ECDSA_P521_OID_VALUE;
            }

            throw new SshException("Unexpected Curve: " + curve);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _digitalSignature?.Dispose();
                _impl.Dispose();
            }
        }
    }
}
