﻿// arithmeticencoder.{hpp, cpp}
using System;
using System.Diagnostics;
using System.IO;

namespace LasZip
{
    internal class ArithmeticEncoder
    {
        private Stream? outStream;

        private readonly byte[] outBuffer;
        private readonly int endBuffer;
        private int outByte;
        private int endByte;
        private UInt32 intervalBase; // named base in C+ but base is a keyword in C#
        private UInt32 length;

        public ArithmeticEncoder()
        {
            this.outBuffer = new byte[2 * EncodeDecode.BufferSize];
            this.endBuffer = 2 * EncodeDecode.BufferSize;
        }

        // Manage encoding
        public bool Init(Stream outStream)
        {
            this.outStream = outStream;
            this.intervalBase = 0;
            this.length = EncodeDecode.MaxLength;
            this.outByte = 0;
            this.endByte = this.endBuffer;
            return true;
        }

        public void Done()
        {
            if (this.outStream == null)
            {
                throw new InvalidOperationException();
            }

            UInt32 initIntervalBase = intervalBase; // done encoding: set final data bytes
            bool anotherByte = true;
            if (length > 2 * EncodeDecode.MinLength)
            {
                this.intervalBase += EncodeDecode.MinLength; // base offset
                this.length = EncodeDecode.MinLength >> 1; // set new length for 1 more byte
            }
            else
            {
                this.intervalBase += EncodeDecode.MinLength >> 1; // interval base offset
                this.length = EncodeDecode.MinLength >> 9; // set new length for 2 more bytes
                anotherByte = false;
            }

            if (initIntervalBase > intervalBase)
            {
                this.PropagateCarry(); // overflow = carry
            }
            this.RenormEncInterval(); // renormalization = output last bytes

            if (this.endByte != this.endBuffer)
            {
                Debug.Assert(this.outByte < EncodeDecode.BufferSize);
                outStream.Write(this.outBuffer, EncodeDecode.BufferSize, EncodeDecode.BufferSize);
            }

            if (this.outByte != 0) { outStream.Write(this.outBuffer, 0, this.outByte); }

            // write two or three zero bytes to be in sync with the decoder's byte reads
            this.outStream.WriteByte(0);
            this.outStream.WriteByte(0);
            if (anotherByte) { this.outStream.WriteByte(0); }

            outStream = null;
        }

        // Manage an entropy model for a single bit
        public static ArithmeticBitModel CreateBitModel()
        {
            return new ArithmeticBitModel();
        }

        public static void InitBitModel(ArithmeticBitModel model)
        {
            model.Init();
        }

        // Manage an entropy model for n symbols (table optional)
        public static ArithmeticModel CreateSymbolModel(UInt32 n)
        {
            return new ArithmeticModel(n, true);
        }

        public static void InitSymbolModel(ArithmeticModel model, UInt32[]? table = null)
        {
            model.Init(table);
        }

        // Encode a bit with modelling
        public void EncodeBit(ArithmeticBitModel model, UInt32 bit)
        {
            Debug.Assert(model != null && (bit <= 1));

            UInt32 x = model.Bit0Prob * (length >> BinaryModels.LengthShift); // product l x p0
            // update interval
            if (bit == 0)
            {
                length = x;
                ++model.Bit0Count;
            }
            else
            {
                UInt32 init_interval_base = intervalBase;
                intervalBase += x;
                length -= x;
                if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            }

            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization
            if (--model.BitsUntilUpdate == 0) { model.Update(); } // periodic model update
        }

        // Encode a symbol with modelling
        public void EncodeSymbol(ArithmeticModel model, UInt32 sym)
        {
            if ((model.Distribution == null) || (model.SymbolCount == null))
            {
                throw new InvalidOperationException();
            }

            Debug.Assert(sym <= model.LastSymbol);
            UInt32 x, init_interval_base = intervalBase;

            // compute products
            if (sym == model.LastSymbol)
            {
                x = model.Distribution[sym] * (length >> GeneralModels.LengthShift);
                intervalBase += x; // update interval
                length -= x; // no product needed
            }
            else
            {
                x = model.Distribution[sym] * (length >>= GeneralModels.LengthShift);
                intervalBase += x; // update interval
                length = model.Distribution[sym + 1] * length - x;
            }

            if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization

            ++model.SymbolCount[sym];
            if (--model.SymbolsUntilUpdate == 0) { model.Update(); } // periodic model update
        }

        // Encode a bit without modelling
        public void WriteBit(UInt32 bit)
        {
            Debug.Assert(bit < 2);

            UInt32 init_interval_base = intervalBase;
            intervalBase += bit * (length >>= 1); // new interval base and length

            if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization
        }

        // Encode bits without modelling
        public void WriteBits(int bits, UInt32 sym)
        {
            Debug.Assert(bits != 0 && (bits <= 32) && (sym < (1u << bits)));

            if (bits > 19)
            {
                WriteShort((UInt16)sym);
                sym = sym >> 16;
                bits = bits - 16;
            }

            UInt32 init_interval_base = intervalBase;
            intervalBase += sym * (length >>= bits); // new interval base and length

            if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization
        }

        // Encode an unsigned char without modelling
        public void WriteByte(byte sym)
        {
            UInt32 init_interval_base = intervalBase;
            intervalBase += (UInt32)(sym) * (length >>= 8); // new interval base and length

            if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization
        }

        // Encode an unsigned short without modelling
        public void WriteShort(UInt16 sym)
        {
            UInt32 init_interval_base = intervalBase;
            intervalBase += (UInt32)(sym) * (length >>= 16); // new interval base and length

            if (init_interval_base > intervalBase) { this.PropagateCarry(); } // overflow = carry
            if (length < EncodeDecode.MinLength) { this.RenormEncInterval(); } // renormalization
        }

        // Encode an unsigned int without modelling
        public void WriteInt(UInt32 sym)
        {
            WriteShort((UInt16)(sym & 0xFFFF)); // lower 16 bits
            WriteShort((UInt16)(sym >> 16)); // UPPER 16 bits
        }

        // Encode a float without modelling
        public unsafe void WriteFloat(float sym) // danger in float reinterpretation
        {
            WriteInt(*(UInt32*)&sym);
        }

        // Encode an unsigned 64 bit int without modelling
        public void WriteInt64(UInt64 sym)
        {
            WriteInt((UInt32)(sym & 0xFFFFFFFF)); // lower 32 bits
            WriteInt((UInt32)(sym >> 32)); // UPPER 32 bits
        }

        // Encode a double without modelling
        public unsafe void WriteDouble(double sym) // danger in float reinterpretation
        {
            WriteInt64(*(UInt64*)&sym);
        }

        private void PropagateCarry()
        {
            int p;
            if (outByte == 0) p = endBuffer - 1;
            else p = outByte - 1;

            while (outBuffer[p] == 0xFFU)
            {
                outBuffer[p] = 0;
                if (p == 0) p = endBuffer - 1;
                else p--;

                Debug.Assert(p >= 0);
                Debug.Assert(p < endBuffer);
                Debug.Assert(outByte < endBuffer);
            }
            outBuffer[p]++;
        }

        private void RenormEncInterval()
        {
            do
            { // output and discard top byte
                Debug.Assert(outByte >= 0);
                Debug.Assert(outByte < endBuffer);
                Debug.Assert(outByte < endByte);
                outBuffer[outByte++] = (byte)(intervalBase >> 24);
                if (outByte == endByte) ManageOutbuffer();
                intervalBase <<= 8;
            } while ((length <<= 8) < EncodeDecode.MinLength); // length multiplied by 256
        }

        private void ManageOutbuffer()
        {
            Debug.Assert(this.outStream != null);

            if (outByte == endBuffer) { outByte = 0; }
            outStream.Write(outBuffer, outByte, EncodeDecode.BufferSize);
            endByte = outByte + EncodeDecode.BufferSize;
            Debug.Assert(endByte > outByte);
            Debug.Assert(outByte < endBuffer);
        }
    }
}
