using System;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Utilities;

namespace Org.BouncyCastle.Pqc.Crypto.Lms
{
    // TODO[api] Make internal
    public static class LMOts
    {
        private static ushort D_PBLC = 0x8080;
        private static int ITER_K = 20;
        private static int ITER_PREV = 23;
        private static int ITER_J = 22;
        
        internal static int SEED_RANDOMISER_INDEX = ~2;
        internal static int MAX_HASH = 32;
        internal static ushort D_MESG = 0x8181;

        public static int Coef(byte[] S, int i, int w)
        {
            int index = (i * w) / 8;
            int digits_per_byte = 8 / w;
            int shift = w * (~i & (digits_per_byte - 1));
            int mask = (1 << w) - 1;

            return (S[index] >> shift) & mask;
        }

        public static int Cksm(byte[] S, int sLen, LMOtsParameters parameters)
        {
            int sum = 0;

            int w = parameters.W;

            // NB assumption about size of "w" not overflowing integer.
            int twoWpow = (1 << w) - 1;

            for (int i = 0; i < (sLen * 8 / parameters.W); i++)
            {
                sum = sum + twoWpow - Coef(S, i, parameters.W);
            }
            return sum << parameters.Ls;
        }

        public static LMOtsPublicKey LmsOtsGeneratePublicKey(LMOtsPrivateKey privateKey)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            byte[] K = LmsOtsGeneratePublicKey(privateKey.Parameters, privateKey.I, privateKey.Q,
                privateKey.MasterSecret);
            return new LMOtsPublicKey(privateKey.Parameters, privateKey.I, privateKey.Q, K);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        internal static byte[] LmsOtsGeneratePublicKey(LMOtsParameters parameters, byte[] I, int q, byte[] masterSecret)
        {
            //
            // Start hash that computes the final value.
            //
            IDigest publicContext = LmsUtilities.GetDigest(parameters);
            byte[] prehashPrefix = Composer.Compose()
                .Bytes(I)
                .U32Str(q)
                .U16Str(D_PBLC)
                .PadUntil(0, 22)
                .Build();
            publicContext.BlockUpdate(prehashPrefix, 0, prehashPrefix.Length);

            IDigest ctx = LmsUtilities.GetDigest(parameters);

            byte[] buf = Composer.Compose()
                .Bytes(I)
                .U32Str(q)
                .PadUntil(0, 23 + ctx.GetDigestSize())
                .Build();

            SeedDerive derive = new SeedDerive(I, masterSecret, LmsUtilities.GetDigest(parameters))
            {
                Q = q,
                J = 0,
            };

            int p = parameters.P;
            int n = parameters.N;
            int twoToWminus1 = (1 << parameters.W) - 1;

            for (ushort i = 0; i < p; i++)
            {
                derive.DeriveSeed(i < p - 1, buf, ITER_PREV); // Private Key!
                Pack.UInt16_To_BE(i, buf, ITER_K);
                for (int j = 0; j < twoToWminus1; j++)
                {
                    buf[ITER_J] = (byte)j;
                    ctx.BlockUpdate(buf, 0, buf.Length);
                    ctx.DoFinal(buf, ITER_PREV);
                }
                publicContext.BlockUpdate(buf, ITER_PREV, n);
            }

            byte[] K = new byte[publicContext.GetDigestSize()];
            publicContext.DoFinal(K, 0);
            return K;
        }

        // TODO[api] Rename
        public static LMOtsSignature lm_ots_generate_signature(LMSigParameters sigParams, LMOtsPrivateKey privateKey,
            byte[][] path, byte[] message, bool preHashed)
        {
            //
            // Add the randomizer.
            //
            byte[] C;
            byte[] Q = new byte[MAX_HASH + 2];

            if (!preHashed)
            {
                LmsContext qCtx = privateKey.GetSignatureContext(sigParams, path);

                LmsUtilities.ByteArray(message, 0, message.Length, qCtx);

                C = qCtx.C;
                Q = qCtx.GetQ();
            }
            else
            {
                int n = privateKey.Parameters.N;

                C = new byte[n];
                Array.Copy(message, 0, Q, 0, n);
            }

            return LMOtsGenerateSignature(privateKey, Q, C);
        }

        public static LMOtsSignature LMOtsGenerateSignature(LMOtsPrivateKey privateKey, byte[] Q, byte[] C)
        {
            LMOtsParameters parameters = privateKey.Parameters;

            int n = parameters.N;
            int p = parameters.P;
            int w = parameters.W;

            byte[] sigComposer = new byte[p * n];

            IDigest ctx = LmsUtilities.GetDigest(parameters);

            SeedDerive derive = privateKey.GetDerivationFunction();

            int cs = Cksm(Q, n, parameters);
            Q[n] = (byte)((cs >> 8) & 0xFF);
            Q[n + 1] = (byte)cs;

#pragma warning disable CS0618 // Type or member is obsolete
            byte[] tmp = Composer.Compose()
                .Bytes(privateKey.I)
                .U32Str(privateKey.Q)
                .PadUntil(0, ITER_PREV + n)
                .Build();
#pragma warning restore CS0618 // Type or member is obsolete

            derive.J = 0;
            for (ushort i = 0; i < p; i++)
            {
                Pack.UInt16_To_BE(i, tmp, ITER_K);
                derive.DeriveSeed(i < p - 1, tmp, ITER_PREV);
                int a = Coef(Q, i, w);
                for (int j = 0; j < a; j++)
                {
                    tmp[ITER_J] = (byte)j;
                    ctx.BlockUpdate(tmp, 0, ITER_PREV + n);
                    ctx.DoFinal(tmp, ITER_PREV);
                }
                Array.Copy(tmp, ITER_PREV, sigComposer, n * i, n);
            }

            return new LMOtsSignature(parameters, C, sigComposer);
        }

        public static bool LMOtsValidateSignature(LMOtsPublicKey publicKey, LMOtsSignature signature, byte[] message,
            bool prehashed)
        {
            if (!signature.ParamType.Equals(publicKey.Parameters)) // todo check
                throw new LmsException("public key and signature ots types do not match");

#pragma warning disable CS0618 // Type or member is obsolete
            return Arrays.AreEqual(LMOtsValidateSignatureCalculate(publicKey, signature, message), publicKey.K);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        public static byte[] LMOtsValidateSignatureCalculate(LMOtsPublicKey publicKey, LMOtsSignature signature, 
            byte[] message)
        {
            LmsContext ctx = publicKey.CreateOtsContext(signature);

            LmsUtilities.ByteArray(message, ctx);

            return LMOtsValidateSignatureCalculate(ctx);
        }

        public static byte[] LMOtsValidateSignatureCalculate(LmsContext context)
        {
            LMOtsPublicKey publicKey = context.PublicKey;
            LMOtsParameters parameters = publicKey.Parameters;
            object sig = context.Signature;
            LMOtsSignature signature;
            if (sig is LmsSignature lmsSignature)
            {
                signature = lmsSignature.OtsSignature;
            }
            else
            {
                signature = (LMOtsSignature)sig;
            }

            int n = parameters.N;
            int w = parameters.W;
            int p = parameters.P;
            byte[] Q = context.GetQ();

            int cs = Cksm(Q, n, parameters);
            Q[n] = (byte)((cs >> 8) & 0xFF);
            Q[n + 1] = (byte)cs;

#pragma warning disable CS0618 // Type or member is obsolete
            byte[] I = publicKey.I;
#pragma warning restore CS0618 // Type or member is obsolete
            int q = publicKey.Q;

            IDigest finalContext = LmsUtilities.GetDigest(parameters);
            LmsUtilities.ByteArray(I, finalContext);
            LmsUtilities.U32Str(q, finalContext);
            LmsUtilities.U16Str((short)D_PBLC, finalContext);

            byte[] tmp = Composer.Compose()
                .Bytes(I)
                .U32Str(q)
                .PadUntil(0, ITER_PREV + n)
                .Build();

            int max_digit = (1 << w) - 1;

#pragma warning disable CS0618 // Type or member is obsolete
            byte[] y = signature.Y;
#pragma warning restore CS0618 // Type or member is obsolete

            IDigest ctx = LmsUtilities.GetDigest(parameters);
            for (ushort i = 0; i < p; i++)
            {
                Pack.UInt16_To_BE(i, tmp, ITER_K);
                Array.Copy(y, i * n, tmp, ITER_PREV, n);
                int a = Coef(Q, i, w);

                for (int j = a; j < max_digit; j++)
                {
                    tmp[ITER_J] = (byte)j;
                    ctx.BlockUpdate(tmp, 0, ITER_PREV + n);
                    ctx.DoFinal(tmp, ITER_PREV);
                }

                finalContext.BlockUpdate(tmp, ITER_PREV, n);
            }

            byte[] K = new byte[n];
            finalContext.DoFinal(K, 0);

            return K;
        }
    }
}
