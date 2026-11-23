namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// Reads the coded picture size out of an H.264 sequence parameter set.
///
/// This matters because decoders pad to macroblock boundaries: a 360-line picture comes back as 368 lines of NV12,
/// and a renderer that trusts the decoder's buffer size shows eight rows of garbage at the bottom of every tile.
/// The SPS also carries the cropping window, which is what gives the true aspect ratio.
/// </summary>
public static class H264Sps
{
    /// <summary>Returns false for anything that is not a well-formed SPS; never throws on hostile input.</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> nal, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (nal.Length < 8 || (nal[0] & 0x1F) != 7)
        {
            return false;
        }

        try
        {
            var reader = new BitReader(nal[1..]);

            var profileIdc = reader.ReadBits(8);
            _ = reader.ReadBits(8); // constraint flags
            _ = reader.ReadBits(8); // level_idc
            _ = reader.ReadUnsignedExpGolomb(); // seq_parameter_set_id

            var chromaFormatIdc = 1; // 4:2:0 when not signalled

            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = reader.ReadUnsignedExpGolomb();

                if (chromaFormatIdc == 3)
                {
                    _ = reader.ReadBit(); // separate_colour_plane_flag
                }

                _ = reader.ReadUnsignedExpGolomb(); // bit_depth_luma_minus8
                _ = reader.ReadUnsignedExpGolomb(); // bit_depth_chroma_minus8
                _ = reader.ReadBit(); // qpprime_y_zero_transform_bypass_flag

                if (reader.ReadBit())
                {
                    var scalingListCount = chromaFormatIdc != 3 ? 8 : 12;

                    for (var index = 0; index < scalingListCount; index++)
                    {
                        if (!reader.ReadBit())
                        {
                            continue;
                        }

                        var size = index < 6 ? 16 : 64;
                        var lastScale = 8;
                        var nextScale = 8;

                        for (var entry = 0; entry < size; entry++)
                        {
                            if (nextScale != 0)
                            {
                                var delta = reader.ReadSignedExpGolomb();
                                nextScale = (lastScale + delta + 256) % 256;
                            }

                            lastScale = nextScale == 0 ? lastScale : nextScale;
                        }
                    }
                }
            }

            _ = reader.ReadUnsignedExpGolomb(); // log2_max_frame_num_minus4
            var picOrderCntType = reader.ReadUnsignedExpGolomb();

            if (picOrderCntType == 0)
            {
                _ = reader.ReadUnsignedExpGolomb(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (picOrderCntType == 1)
            {
                _ = reader.ReadBit(); // delta_pic_order_always_zero_flag
                _ = reader.ReadSignedExpGolomb(); // offset_for_non_ref_pic
                _ = reader.ReadSignedExpGolomb(); // offset_for_top_to_bottom_field
                var cycle = reader.ReadUnsignedExpGolomb();

                for (var index = 0; index < cycle; index++)
                {
                    _ = reader.ReadSignedExpGolomb();
                }
            }

            _ = reader.ReadUnsignedExpGolomb(); // max_num_ref_frames
            _ = reader.ReadBit(); // gaps_in_frame_num_value_allowed_flag

            var widthInMbs = reader.ReadUnsignedExpGolomb() + 1;
            var heightInMapUnits = reader.ReadUnsignedExpGolomb() + 1;
            var frameMbsOnly = reader.ReadBit();

            if (!frameMbsOnly)
            {
                _ = reader.ReadBit(); // mb_adaptive_frame_field_flag
            }

            _ = reader.ReadBit(); // direct_8x8_inference_flag

            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;

            if (reader.ReadBit())
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            var chromaArrayType = chromaFormatIdc == 3 ? 0 : chromaFormatIdc;
            var subWidth = chromaArrayType switch { 1 => 2, 2 => 2, _ => 1 };
            var subHeight = chromaArrayType switch { 1 => 2, 2 => 1, _ => 1 };

            var frameMbsOnlyFlag = frameMbsOnly ? 1 : 0;
            var codedWidth = widthInMbs * 16;
            var codedHeight = (2 - frameMbsOnlyFlag) * heightInMapUnits * 16;

            var cropUnitX = subWidth;
            var cropUnitY = subHeight * (2 - frameMbsOnlyFlag);

            width = codedWidth - ((cropLeft + cropRight) * cropUnitX);
            height = codedHeight - ((cropTop + cropBottom) * cropUnitY);

            return width > 0 && height > 0 && width <= 8192 && height <= 8192;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    /// <summary>MSB-first bit reader with H.264 Exp-Golomb decoding.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        public int ReadBits(int count)
        {
            var value = 0;

            for (var index = 0; index < count; index++)
            {
                var byteIndex = _bit >> 3;
                if (byteIndex >= _data.Length)
                {
                    throw new InvalidOperationException("SPS ended early.");
                }

                var bit = (_data[byteIndex] >> (7 - (_bit & 7))) & 1;
                value = (value << 1) | bit;
                _bit++;
            }

            return value;
        }

        public bool ReadBit() => ReadBits(1) == 1;

        public int ReadUnsignedExpGolomb()
        {
            var leadingZeros = 0;

            while (ReadBit() == false)
            {
                leadingZeros++;
                if (leadingZeros > 31)
                {
                    throw new InvalidOperationException("Exp-Golomb code is too long.");
                }
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            return (1 << leadingZeros) - 1 + ReadBits(leadingZeros);
        }

        public int ReadSignedExpGolomb()
        {
            var value = ReadUnsignedExpGolomb();
            var magnitude = (value + 1) / 2;
            return value % 2 == 0 ? -magnitude : magnitude;
        }
    }
}
