export type ByteInput = ArrayBuffer | ArrayBufferView;
export type Channels = 3 | 4;
export type Tier = 'Fast' | 'Compact';
export interface FicInfo {
    width: number;
    height: number;
    channels: Channels;
    tier: Tier;
    exifLength: number;
}
export interface DecodedImage {
    pixels: Uint8Array;
    width: number;
    height: number;
    channels: Channels;
    exif: Uint8Array;
    tier: Tier;
}
export declare function getInfo(input: ByteInput): FicInfo;
export declare function decode(input: ByteInput): DecodedImage;
export declare function decodeAsync(input: ByteInput): Promise<DecodedImage>;
export declare function encode(input: ByteInput, width: number, height: number, channels?: Channels, exifInput?: ByteInput): Uint8Array;
