﻿// arithmeticdecoder.{hpp, cpp}
using System;
using System.Diagnostics;
using System.IO;

namespace LasZip
{
    internal class ArithmeticDecoder
    {
        private Stream? inStream;
        private UInt32 value;
        private UInt32 length;

        public ArithmeticDecoder()
        {
            this.inStream = null;
        }

        // manage decoding
        public bool Init(Stream inStream, bool reallyInit = true)
        {
            this.inStream = inStream;
            if (reallyInit)
            {
                length = EncodeDecode.MaxLength;
                value = (UInt32)inStream.ReadByte() << 24;
                value |= (UInt32)inStream.ReadByte() << 16;
                value |= (UInt32)inStream.ReadByte() << 8;
                value |= (UInt32)inStream.ReadByte();
            }

            return true;
        }

        public void Done()
        {
            inStream = null;
        }

        // Manage an entropy model for a single bit
        public static ArithmeticBitModel CreateBitModel()
        {
            return new ArithmeticBitModel();
        }

        public static void InitBitModel(ArithmeticBitModel m)
        {
            m.Init();
        }

        // Manage an entropy model for n symbols (table optional)
        public static ArithmeticModel CreateSymbolModel(UInt32 n)
        {
            return new ArithmeticModel(n, false);
        }

        public static void InitSymbolModel(ArithmeticModel model, UInt32[]? table = null)
        {
            model.Init(table);
        }

        // Decode a bit with modelling
        public UInt32 DecodeBit(ArithmeticBitModel m)
        {
            Debug.Assert(m != null);

            UInt32 x = m.Bit0Prob * (length >> BinaryModels.LengthShift); // product l x p0
            UInt32 sym = (value >= x) ? 1u : 0u; // decision

            // update & shift interval
            if (sym == 0)
            {
                length = x;
                ++m.Bit0Count;
            }
            else
            {
                value -= x; // shifted interval base = 0
                length -= x;
            }

            if (length < EncodeDecode.MinLength) RenormDecInterval(); // renormalization
            if (--m.BitsUntilUpdate == 0) m.Update(); // periodic model update

            return sym; // return data bit value
        }

        // Decode a symbol with modelling
        public UInt32 DecodeSymbol(ArithmeticModel model)
        {
            if ((model.Distribution == null) || (model.SymbolCount == null))
            {
                throw new ArgumentOutOfRangeException(nameof(model));
            }

            UInt32 n, sym, x, y = length;
            if (model.DecoderTable != null)
            {
                // use table look-up for faster decoding
                UInt32 dv = value / (length >>= GeneralModels.LengthShift);
                UInt32 t = dv >> model.TableShift;

                sym = model.DecoderTable[t]; // initial decision based on table look-up
                n = model.DecoderTable[t + 1] + 1;

                while (n > sym + 1)
                { // finish with bisection search
                    UInt32 k = (sym + n) >> 1;
                    if (model.Distribution[k] > dv) { n = k; } else { sym = k; }
                }

                // compute products
                x = model.Distribution[sym] * length;
                if (sym != model.LastSymbol) { y = model.Distribution[sym + 1] * length; }
            }
            else
            { // decode using only multiplications
                x = sym = 0;
                length >>= GeneralModels.LengthShift;
                UInt32 k = (n = model.Symbols) >> 1;

                // decode via bisection search
                do
                {
                    UInt32 z = length * model.Distribution[k];
                    if (z > value)
                    {
                        n = k;
                        y = z; // value is smaller
                    }
                    else
                    {
                        sym = k;
                        x = z; // value is larger or equal
                    }
                } while ((k = (sym + n) >> 1) != sym);
            }

            value -= x; // update interval
            length = y - x;

            if (length < EncodeDecode.MinLength) RenormDecInterval(); // renormalization

            ++model.SymbolCount[sym];
            if (--model.SymbolsUntilUpdate == 0) model.Update(); // periodic model update

            Debug.Assert(sym < model.Symbols);

            return sym;
        }

        // Decode a bit without modelling
        public UInt32 ReadBit()
        {
            UInt32 sym = value / (length >>= 1); // decode symbol, change length
            value -= length * sym; // update interval

            if (length < EncodeDecode.MinLength) RenormDecInterval(); // renormalization

            if (sym >= 2)
            {
                throw new IOException("4711");
            }

            return sym;
        }

        // Decode bits without modelling
        public UInt32 ReadBits(UInt32 bits)
        {
            Debug.Assert(bits != 0 && (bits <= 32));

            if (bits > 19)
            {
                UInt32 tmp = ReadUInt16();
                bits = bits - 16;
                UInt32 tmp1 = ReadBits(bits) << 16;
                return (tmp1 | tmp);
            }

            UInt32 sym = value / (length >>= (int)bits); // decode symbol, change length
            value -= length * sym; // update interval

            if (length < EncodeDecode.MinLength) RenormDecInterval(); // renormalization

            if (sym >= (1U << (int)bits))
            {
                throw new FileLoadException("4711");
            }

            return sym;
        }

        // Decode an unsigned char without modelling
        public byte ReadByte()
        {
            UInt32 sym = value / (length >>= 8); // decode symbol, change length
            value -= length * sym; // update interval

            if (length < EncodeDecode.MinLength) RenormDecInterval(); // renormalization

            if (sym >= (1U << 8))
            {
                throw new IOException("4711");
            }

            return (byte)sym;
        }

        // Decode an unsigned short without modelling
        public UInt16 ReadUInt16()
        {
            UInt32 sym = value / (length >>= 16); // decode symbol, change length
            value -= length * sym; // update interval

            if (length < EncodeDecode.MinLength) 
                this.RenormDecInterval(); // renormalization

            if (sym >= (1U << 16))
            {
                throw new IOException("4711");
            }

            return (UInt16)sym;
        }

        // Decode an unsigned int without modelling
        public UInt32 ReadUInt32()
        {
            UInt32 lowerInt = this.ReadUInt16();
            UInt32 upperInt = this.ReadUInt16();
            return (upperInt << 16) | lowerInt;
        }

        // Decode a float without modelling
        public float ReadFloat() // danger in float reinterpretation
        {
            UInt32 ret = this.ReadUInt32();
            return (float)ret;
        }

        // Decode an unsigned 64 bit int without modelling
        public UInt64 ReadUInt64()
        {
            UInt64 lowerInt = this.ReadUInt32();
            UInt64 upperInt = this.ReadUInt32();
            return (upperInt << 32) | lowerInt;
        }

        // Decode a double without modelling
        public double ReadDouble() // danger in float reinterpretation
        {
            UInt64 ret = this.ReadUInt64();
            return (double)ret;
        }

        private void RenormDecInterval()
        {
            Debug.Assert(this.inStream != null);

            do
            { 
                // read least-significant byte
                value = (value << 8) | (UInt32)this.inStream.ReadByte();
            } 
            while ((length <<= 8) < EncodeDecode.MinLength); // length multiplied by 256
        }
    }
}
