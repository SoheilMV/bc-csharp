using System;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.CryptoPro;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.Rosstandart;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace Org.BouncyCastle.Pkcs
{
    public static class PrivateKeyInfoFactory
    {
        public static PrivateKeyInfo CreatePrivateKeyInfo(
            AsymmetricKeyParameter privateKey)
        {
            return CreatePrivateKeyInfo(privateKey, null);
        }

        /**
         * Create a PrivateKeyInfo representation of a private key with attributes.
         *
         * @param privateKey the key to be encoded into the info object.
         * @param attributes the set of attributes to be included.
         * @return the appropriate PrivateKeyInfo
         * @throws java.io.IOException on an error encoding the key
         */
        public static PrivateKeyInfo CreatePrivateKeyInfo(AsymmetricKeyParameter privateKey, Asn1Set attributes)
        {
            if (privateKey == null)
                throw new ArgumentNullException("privateKey");
            if (!privateKey.IsPrivate)
                throw new ArgumentException("Public key passed - private key expected", "privateKey");

            if (privateKey is ElGamalPrivateKeyParameters)
            {
                ElGamalPrivateKeyParameters _key = (ElGamalPrivateKeyParameters)privateKey;
                ElGamalParameters egp = _key.Parameters;
                return new PrivateKeyInfo(
                    new AlgorithmIdentifier(OiwObjectIdentifiers.ElGamalAlgorithm, new ElGamalParameter(egp.P, egp.G).ToAsn1Object()),
                    new DerInteger(_key.X),
                    attributes);
            }

            if (privateKey is DsaPrivateKeyParameters)
            {
                DsaPrivateKeyParameters _key = (DsaPrivateKeyParameters)privateKey;
                DsaParameters dp = _key.Parameters;
                return new PrivateKeyInfo(
                    new AlgorithmIdentifier(X9ObjectIdentifiers.IdDsa, new DsaParameter(dp.P, dp.Q, dp.G).ToAsn1Object()),
                    new DerInteger(_key.X),
                    attributes);
            }

            if (privateKey is DHPrivateKeyParameters)
            {
                DHPrivateKeyParameters _key = (DHPrivateKeyParameters)privateKey;

                DHParameter p = new DHParameter(
                    _key.Parameters.P, _key.Parameters.G, _key.Parameters.L);

                return new PrivateKeyInfo(
                    new AlgorithmIdentifier(_key.AlgorithmOid, p.ToAsn1Object()),
                    new DerInteger(_key.X),
                    attributes);
            }

            if (privateKey is RsaKeyParameters)
            {
                AlgorithmIdentifier algID = new AlgorithmIdentifier(
                    PkcsObjectIdentifiers.RsaEncryption, DerNull.Instance);

                RsaPrivateKeyStructure keyStruct;
                if (privateKey is RsaPrivateCrtKeyParameters)
                {
                    RsaPrivateCrtKeyParameters _key = (RsaPrivateCrtKeyParameters)privateKey;

                    keyStruct = new RsaPrivateKeyStructure(
                        _key.Modulus,
                        _key.PublicExponent,
                        _key.Exponent,
                        _key.P,
                        _key.Q,
                        _key.DP,
                        _key.DQ,
                        _key.QInv);
                }
                else
                {
                    RsaKeyParameters _key = (RsaKeyParameters) privateKey;

                    keyStruct = new RsaPrivateKeyStructure(
                        _key.Modulus,
                        BigInteger.Zero,
                        _key.Exponent,
                        BigInteger.Zero,
                        BigInteger.Zero,
                        BigInteger.Zero,
                        BigInteger.Zero,
                        BigInteger.Zero);
                }

                return new PrivateKeyInfo(algID, keyStruct.ToAsn1Object(), attributes);
            }

            if (privateKey is ECPrivateKeyParameters priv)
            {
                var pub = ECKeyPairGenerator.GetCorrespondingPublicKey(priv);
                var q = pub.Q;

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
                int encodedLength = q.GetEncodedLength(false);
                Span<byte> pubEncoding = encodedLength <= 512
                    ? stackalloc byte[encodedLength]
                    : new byte[encodedLength];
                q.EncodeTo(false, pubEncoding);
#else
                byte[] pubEncoding = q.GetEncoded(false);
#endif

                DerBitString publicKey = new DerBitString(pubEncoding);

                ECDomainParameters dp = priv.Parameters;

                // ECGOST3410
                if (dp is ECGost3410Parameters domainParameters)
                {
                    Gost3410PublicKeyAlgParameters gostParams = new Gost3410PublicKeyAlgParameters(
                        (domainParameters).PublicKeyParamSet,
                        (domainParameters).DigestParamSet,
                        (domainParameters).EncryptionParamSet);

                    bool is512 = priv.D.BitLength > 256;
                    DerObjectIdentifier identifier = (is512) ?
                        RosstandartObjectIdentifiers.id_tc26_gost_3410_12_512 :
                        RosstandartObjectIdentifiers.id_tc26_gost_3410_12_256;
                    int size = (is512) ? 64 : 32;

                    byte[] encKey = new byte[size];

                    ExtractBytes(encKey, size, 0, priv.D);

                    return new PrivateKeyInfo(new AlgorithmIdentifier(identifier, gostParams), new DerOctetString(encKey));
                } 


                int orderBitLength = dp.N.BitLength;

                AlgorithmIdentifier algID;
                ECPrivateKeyStructure ec;

                if (priv.AlgorithmName == "ECGOST3410")
                {
                    if (priv.PublicKeyParamSet == null)
                        throw new NotImplementedException("Not a CryptoPro parameter set");

                    Gost3410PublicKeyAlgParameters gostParams = new Gost3410PublicKeyAlgParameters(
                        priv.PublicKeyParamSet, CryptoProObjectIdentifiers.GostR3411x94CryptoProParamSet);

                    algID = new AlgorithmIdentifier(CryptoProObjectIdentifiers.GostR3410x2001, gostParams);

                    // TODO Do we need to pass any parameters here?
                    ec = new ECPrivateKeyStructure(orderBitLength, priv.D, publicKey, null);
                }
                else
                {
                    X962Parameters x962;
                    if (priv.PublicKeyParamSet == null)
                    {
                        X9ECParameters ecP = new X9ECParameters(dp.Curve, new X9ECPoint(dp.G, false), dp.N, dp.H,
                            dp.GetSeed());
                        x962 = new X962Parameters(ecP);
                    }
                    else
                    {
                        x962 = new X962Parameters(priv.PublicKeyParamSet);
                    }

                    ec = new ECPrivateKeyStructure(orderBitLength, priv.D, publicKey, x962);

                    algID = new AlgorithmIdentifier(X9ObjectIdentifiers.IdECPublicKey, x962);
                }

                return new PrivateKeyInfo(algID, ec, attributes);
            }

            if (privateKey is Gost3410PrivateKeyParameters)
            {
                Gost3410PrivateKeyParameters _key = (Gost3410PrivateKeyParameters)privateKey;

                if (_key.PublicKeyParamSet == null)
                    throw new NotImplementedException("Not a CryptoPro parameter set");

                // must be little endian
                byte[] keyEnc = Arrays.ReverseInPlace(_key.X.ToByteArrayUnsigned());

                Gost3410PublicKeyAlgParameters algParams = new Gost3410PublicKeyAlgParameters(
                    _key.PublicKeyParamSet, CryptoProObjectIdentifiers.GostR3411x94CryptoProParamSet, null);

                AlgorithmIdentifier algID = new AlgorithmIdentifier(
                    CryptoProObjectIdentifiers.GostR3410x94,
                    algParams.ToAsn1Object());

                return new PrivateKeyInfo(algID, new DerOctetString(keyEnc), attributes);
            }

            if (privateKey is X448PrivateKeyParameters)
            {
                X448PrivateKeyParameters key = (X448PrivateKeyParameters)privateKey;

                return new PrivateKeyInfo(new AlgorithmIdentifier(EdECObjectIdentifiers.id_X448),
                    new DerOctetString(key.GetEncoded()), attributes, key.GeneratePublicKey().GetEncoded());
            }

            if (privateKey is X25519PrivateKeyParameters)
            {
                X25519PrivateKeyParameters key = (X25519PrivateKeyParameters)privateKey;

                return new PrivateKeyInfo(new AlgorithmIdentifier(EdECObjectIdentifiers.id_X25519),
                    new DerOctetString(key.GetEncoded()), attributes, key.GeneratePublicKey().GetEncoded());
            }

            if (privateKey is Ed448PrivateKeyParameters)
            {
                Ed448PrivateKeyParameters key = (Ed448PrivateKeyParameters)privateKey;

                return new PrivateKeyInfo(new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed448),
                    new DerOctetString(key.GetEncoded()), attributes, key.GeneratePublicKey().GetEncoded());
            }

            if (privateKey is Ed25519PrivateKeyParameters)
            {
                Ed25519PrivateKeyParameters key = (Ed25519PrivateKeyParameters)privateKey;

                return new PrivateKeyInfo(new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed25519),
                    new DerOctetString(key.GetEncoded()), attributes, key.GeneratePublicKey().GetEncoded());
            }

            if (privateKey is MLDsaPrivateKeyParameters mlDsaKey)
            {
                var algID = new AlgorithmIdentifier(mlDsaKey.Parameters.Oid);

                var privateKeyAsn1 = GetMLDsaPrivateKeyAsn1(mlDsaKey);

                // NOTE: The public key can be derived from the private key
                byte[] publicKey = null;

                return new PrivateKeyInfo(algID, privateKeyAsn1, attributes, publicKey);
            }

            if (privateKey is MLKemPrivateKeyParameters mlKemKey)
            {
                var algID = new AlgorithmIdentifier(mlKemKey.Parameters.Oid);

                var privateKeyAsn1 = GetMLKemPrivateKeyAsn1(mlKemKey);

                // NOTE: The private key already includes the public key
                byte[] publicKey = null;

                return new PrivateKeyInfo(algID, privateKeyAsn1, attributes, publicKey);
            }

            if (privateKey is SlhDsaPrivateKeyParameters slhDsaKey)
            {
                var algID = new AlgorithmIdentifier(slhDsaKey.Parameters.Oid);

                // NOTE: The private key already includes the public key
                DerBitString publicKey = null;

                return PrivateKeyInfo.Create(algID, new DerOctetString(slhDsaKey.GetEncoded()), attributes, publicKey);
            }

            throw new ArgumentException("Class provided is not convertible: " + Platform.GetTypeName(privateKey));
        }

        public static PrivateKeyInfo CreatePrivateKeyInfo(
            char[]					passPhrase,
            EncryptedPrivateKeyInfo	encInfo)
        {
            return CreatePrivateKeyInfo(passPhrase, false, encInfo);
        }

        public static PrivateKeyInfo CreatePrivateKeyInfo(
            char[]					passPhrase,
            bool					wrongPkcs12Zero,
            EncryptedPrivateKeyInfo	encInfo)
        {
            AlgorithmIdentifier algID = encInfo.EncryptionAlgorithm;

            IBufferedCipher cipher = PbeUtilities.CreateEngine(algID) as IBufferedCipher;
            if (cipher == null)
                throw new Exception("Unknown encryption algorithm: " + algID.Algorithm);

            ICipherParameters cipherParameters = PbeUtilities.GenerateCipherParameters(
                algID, passPhrase, wrongPkcs12Zero);
            cipher.Init(false, cipherParameters);
            byte[] keyBytes = cipher.DoFinal(encInfo.GetEncryptedData());

            return PrivateKeyInfo.GetInstance(keyBytes);
        }

        private static void ExtractBytes(byte[] encKey, int size, int offSet, BigInteger bI)
        {
            byte[] val = bI.ToByteArray();
            if (val.Length < size)
            {
                byte[] tmp = new byte[size];
                Array.Copy(val, 0, tmp, tmp.Length - val.Length, val.Length);
                val = tmp;
            }

            for (int i = 0; i != size; i++)
            {
                encKey[offSet + i] = val[val.Length - 1 - i];
            }
        }

        private static Asn1Encodable GetMLDsaPrivateKeyAsn1(MLDsaPrivateKeyParameters key)
        {
            switch (key.PreferredFormat)
            {
            case MLDsaPrivateKeyParameters.Format.EncodingOnly:
                return new DerOctetString(key.GetEncoded());
            case MLDsaPrivateKeyParameters.Format.SeedOnly:
                return new DerTaggedObject(false, 0, new DerOctetString(key.GetSeed()));
            case MLDsaPrivateKeyParameters.Format.SeedAndEncoding:
            default:
                return new DerSequence(new DerOctetString(key.GetSeed()), new DerOctetString(key.GetEncoded()));
            }
        }

        private static Asn1Encodable GetMLKemPrivateKeyAsn1(MLKemPrivateKeyParameters key)
        {
            switch (key.PreferredFormat)
            {
            case MLKemPrivateKeyParameters.Format.EncodingOnly:
                return new DerOctetString(key.GetEncoded());
            case MLKemPrivateKeyParameters.Format.SeedOnly:
                return new DerTaggedObject(false, 0, new DerOctetString(key.GetSeed()));
            case MLKemPrivateKeyParameters.Format.SeedAndEncoding:
            default:
                return new DerSequence(new DerOctetString(key.GetSeed()), new DerOctetString(key.GetEncoded()));
            }
        }
    }
}
