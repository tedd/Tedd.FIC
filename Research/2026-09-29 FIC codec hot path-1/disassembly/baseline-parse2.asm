; Assembly listing for method Tedd.FIC.QpCodec+Enc`1[Tedd.FIC.BaseW]:Parse2(byref,byref,byref,byref,int,int,byref):nint (FullOpts)
; Emitting BLENDED_CODE for x64 + VEX on Windows
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 6 single block inlinees; 10 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     r12
       push     rdi
       push     rsi
       push     rbx
       sub      rsp, 216
       lea      rbp, [rsp+0x20]
       mov      rax, 0x48DAEBEF2F2B
       mov      qword ptr [rbp+0xB0], rax

G_M000_IG02:                ;; offset=0x0029
       mov      rbx, bword ptr [rbp+0x130]
       mov      esi, dword ptr [rbp+0x128]
       mov      dword ptr [rbp+0x54], esi
       mov      edi, dword ptr [rbp+0x120]
       mov      dword ptr [rbp+0x58], edi
       mov      r14, r9
       mov      bword ptr [rbp+0x08], r14
       mov      bword ptr [rbp+0x10], rdx
       mov      r13, rcx
       mov      bword ptr [rbp+0x18], r13
       mov      r12d, dword ptr [r13+0x50]
       mov      rcx, -0x1100
       add      rcx, rsp
       jb       SHORT G_M000_IG03
       xor      ecx, ecx

G_M000_IG03:                ;; offset=0x0069
       test     dword ptr [rsp], esp
       sub      rsp, 0x1000
       cmp      rsp, rcx
       jae      SHORT G_M000_IG03
       mov      rsp, rcx
       lea      rcx, [rsp+0x20]
       mov      qword ptr [rbp+0x48], rcx
       mov      edx, 0x1100
       call     CORINFO_HELP_MEMZERO
       mov      rcx, qword ptr [rbp+0x48]
       mov      rax, rcx
       mov      bword ptr [rbp+0x20], rax
       mov      rcx, rax
       mov      edx, 0x1100
       call     [System.SpanHelpers:ClearWithoutReferences(byref,nuint)]
       mov      r8, bword ptr [rbp+0x20]
       mov      bword ptr [rbp+0x40], r8
       lea      r10, bword ptr [r8+0x100]
       mov      bword ptr [rbp+0x38], r10
       mov      r9d, 0xFFFFFFFFFF000000
       xor      ecx, ecx
       xor      r11d, r11d
       mov      dword ptr [rbp+0x98], -1
       xor      eax, eax
       mov      qword ptr [rbp+0x90], rax
       mov      r10, qword ptr [r13+0x58]
       mov      qword ptr [rbp+0x88], r10
       mov      r10, 0x7FFFFFFFFFFFFFFF
       cmp      qword ptr [rbp+0x88], r10
       jne      SHORT G_M000_IG05

G_M000_IG04:                ;; offset=0x00F6
       mov      r10d, 0x7FFFFFFF
       jmp      SHORT G_M000_IG06
       align    [0 bytes for IG13]

G_M000_IG05:                ;; offset=0x00FE
       mov      eax, edi
       cdq
       idiv     edx:eax, dword ptr [(reloc 0x7ffc571cb1b8)]
       mov      r10d, eax

G_M000_IG06:                ;; offset=0x010A
       mov      eax, r10d
       vmovsd   xmm0, qword ptr [(reloc 0x7ffc571cb1b0)]
       vmulsd   xmm0, xmm0, qword ptr [reloc @RWD00]
       vcmpordsd xmm1, xmm0, xmm0
       vandpd   xmm1, xmm1, xmm0
       mov      r10, 0x7FFFFFFFFFFFFFFF
       vcvttsd2si rdx, xmm1
       vucomisd xmm0, qword ptr [reloc @RWD08]
       cmovb    r10, rdx
       mov      qword ptr [rbp+0x78], r10
       cmp      r11d, edi
       jge      G_M000_IG74

G_M000_IG07:                ;; offset=0x014E
       mov      edx, r11d
       sar      edx, 6
       mov      r10d, dword ptr [rbp+0x98]
       cmp      edx, r10d
       je       SHORT G_M000_IG11

G_M000_IG08:                ;; offset=0x0160
       mov      r10d, edx
       mov      dword ptr [rbp+0x98], r10d
       mov      edx, r10d
       imul     edx, r12d
       lea      edx, [rdx+r12-0x01]
       movsxd   rdx, edx
       mov      rdx, qword ptr [r14+8*rdx]
       cmp      r11d, eax
       jge      SHORT G_M000_IG10

G_M000_IG09:                ;; offset=0x0182
       mov      qword ptr [rbp+0x90], rdx
       mov      r10d, dword ptr [rbp+0x98]
       jmp      SHORT G_M000_IG11

G_M000_IG10:                ;; offset=0x0192
       movsxd   rax, edi
       imul     rax, rcx
       shl      rax, 10
       mov      rsi, qword ptr [rbp+0x88]
       imul     rsi, qword ptr [rbp+0x78]
       movsxd   r8, r11d
       imul     r8, rsi
       cmp      rax, r8
       jg       G_M000_IG77
       mov      eax, edi
       sar      eax, 3
       add      eax, r11d
       mov      dword ptr [rbp+0x84], eax
       mov      qword ptr [rbp+0x90], rdx
       mov      eax, dword ptr [rbp+0x84]
       mov      r10d, dword ptr [rbp+0x98]

G_M000_IG11:                ;; offset=0x01DB
       mov      rdx, qword ptr [rbp+0x90]
       bt       rdx, r11
       jae      G_M000_IG41

G_M000_IG12:                ;; offset=0x01EC
       mov      dword ptr [rbp+0x84], eax
       mov      r9, bword ptr [r13+0x40]
       mov      bword ptr [rbp+0x28], r9
       mov      esi, dword ptr [r13+0x48]
       mov      r8d, r11d
       and      r8d, 63
       mov      eax, r11d
       sar      eax, 6
       imul     eax, r12d
       cdqe
       lea      rax, bword ptr [r14+8*rax]
       xor      edx, edx
       xor      r15d, r15d
       test     esi, esi
       mov      dword ptr [rbp+0x98], r10d
       je       SHORT G_M000_IG17

G_M000_IG13:                ;; offset=0x0225
       movsxd   r10, dword ptr [r9+4*r15]
       shrx     r10, qword ptr [rax+8*r10], r8
       not      r10
       tzcnt    r10, r10
       mov      r14d, r15d
       neg      r14d
       add      r14d, 15
       shl      r10d, 4
       or       r10d, r14d
       cmp      edx, r10d
       jl       SHORT G_M000_IG15

G_M000_IG14:                ;; offset=0x024D
       jmp      SHORT G_M000_IG16

G_M000_IG15:                ;; offset=0x024F
       mov      edx, r10d

G_M000_IG16:                ;; offset=0x0252
       inc      r15d
       cmp      r15d, esi
       jl       SHORT G_M000_IG13

G_M000_IG17:                ;; offset=0x025A
       mov      r10d, edx
       sar      r10d, 4
       mov      r14d, r8d
       neg      r14d
       add      r14d, 64
       cmp      r14d, r10d
       jg       G_M000_IG33
       xor      r10d, r10d
       xor      r14d, r14d
       mov      dword ptr [rbp+0x74], r14d
       test     esi, esi
       jne      SHORT G_M000_IG19

G_M000_IG18:                ;; offset=0x0282
       jmp      G_M000_IG34
       align    [0 bytes for IG31]

G_M000_IG19:                ;; offset=0x0287
       xor      edx, edx
       jmp      SHORT G_M000_IG28

G_M000_IG20:                ;; offset=0x028B
       not      r9
       tzcnt    r9, r9
       add      r9d, r13d
       mov      r13d, r9d
       cmp      r13d, r14d
       jg       SHORT G_M000_IG21
       mov      r9d, r13d
       jmp      SHORT G_M000_IG22

G_M000_IG21:                ;; offset=0x02A3
       mov      r9d, r14d

G_M000_IG22:                ;; offset=0x02A6
       mov      edi, r9d
       jmp      SHORT G_M000_IG25

G_M000_IG23:                ;; offset=0x02AB
       cmp      r13d, r14d
       jg       SHORT G_M000_IG24
       mov      edi, r13d
       jmp      SHORT G_M000_IG25

G_M000_IG24:                ;; offset=0x02B5
       mov      edi, r14d

G_M000_IG25:                ;; offset=0x02B8
       cmp      edi, r10d
       jle      SHORT G_M000_IG26
       mov      r10d, edi
       mov      r14d, r15d
       mov      dword ptr [rbp+0x74], r14d

G_M000_IG26:                ;; offset=0x02C7
       add      rdx, 4
       dec      esi
       mov      r9, bword ptr [rbp+0x28]
       je       G_M000_IG34

G_M000_IG27:                ;; offset=0x02D7
       mov      edi, dword ptr [rbp+0x58]

G_M000_IG28:                ;; offset=0x02DA
       mov      r15d, dword ptr [r9+rdx]
       lea      r14d, [r11+r10]
       cmp      r14d, edi
       jge      SHORT G_M000_IG26

G_M000_IG29:                ;; offset=0x02E7
       mov      edi, r14d
       sar      edi, 6
       imul     edi, r12d
       add      edi, r15d
       movsxd   rdi, edi
       mov      r13, bword ptr [rbp+0x08]
       mov      rdi, qword ptr [r13+8*rdi]
       bt       rdi, r14
       jae      SHORT G_M000_IG26
       mov      rdi, bword ptr [rbp+0x18]
       cmp      r15d, dword ptr [rdi+0x08]
       jae      G_M000_IG80
       mov      r14, bword ptr [rdi]
       mov      r13d, r15d
       mov      r14d, dword ptr [r14+4*r13]
       mov      r13d, r15d
       shrx     r13, qword ptr [rax+8*r13], r8
       not      r13
       tzcnt    r13, r13
       mov      edi, r8d
       neg      edi
       add      edi, 64
       cmp      edi, r13d
       jge      SHORT G_M000_IG30
       mov      r13d, r8d
       neg      r13d
       add      r13d, 64

G_M000_IG30:                ;; offset=0x0346
       mov      edi, r8d
       neg      edi
       add      edi, 64
       cmp      edi, r13d
       jg       G_M000_IG23
       cmp      r13d, r14d
       jge      G_M000_IG23
       mov      edi, r15d

G_M000_IG31:                ;; offset=0x0363
       movsxd   r9, r12d
       add      rdi, r9
       mov      r9, qword ptr [rax+8*rdi]
       cmp      r9, -1
       jne      G_M000_IG20
       add      r13d, 64
       cmp      r13d, r14d
       jl       SHORT G_M000_IG31

G_M000_IG32:                ;; offset=0x0380
       mov      edi, r14d
       jmp      G_M000_IG25

G_M000_IG33:                ;; offset=0x0388
       and      edx, 15
       neg      edx
       add      edx, 15
       cmp      edx, esi
       jae      G_M000_IG80
       mov      r14d, dword ptr [r9+4*rdx]
       mov      dword ptr [rbp+0x74], r14d

G_M000_IG34:                ;; offset=0x03A0
       mov      r14d, dword ptr [rbp+0x74]
       mov      r13, bword ptr [rbp+0x18]
       cmp      r14d, dword ptr [r13+0x08]
       jae      G_M000_IG80
       mov      rdx, bword ptr [r13]
       mov      eax, r14d
       mov      edx, dword ptr [rdx+4*rax]
       cmp      r10d, edx
       jg       SHORT G_M000_IG35
       jmp      SHORT G_M000_IG36
       align    [12 bytes for IG37]

G_M000_IG35:                ;; offset=0x03CF
       mov      r10d, edx

G_M000_IG36:                ;; offset=0x03D2
       lea      rdx, bword ptr [r13+0x20]
       cmp      r14d, dword ptr [rdx+0x08]
       jae      G_M000_IG80
       mov      rdx, bword ptr [rdx]
       mov      eax, r14d
       mov      edx, dword ptr [rdx+4*rax]
       cmp      r10d, edx
       jle      SHORT G_M000_IG39
       lea      rax, [rcx+0x01]
       mov      rsi, rax
       lea      rax, bword ptr [r13+0x30]
       cmp      r14d, dword ptr [rax+0x08]
       jae      G_M000_IG80
       mov      rax, bword ptr [rax]
       mov      r8d, r14d
       movzx    rax, byte  ptr [rax+4*r8]
       mov      byte  ptr [rbx+rcx], al
       mov      ecx, r10d
       sub      ecx, edx
       dec      ecx
       cmp      ecx, 128
       jl       SHORT G_M000_IG38

G_M000_IG37:                ;; offset=0x0420
       mov      rdx, rsi
       lea      rsi, [rdx+0x01]
       mov      eax, ecx
       or       eax, 128
       mov      byte  ptr [rbx+rdx], al
       sar      ecx, 7
       cmp      ecx, 128
       jge      SHORT G_M000_IG37

G_M000_IG38:                ;; offset=0x043C
       mov      rdx, rsi
       lea      rsi, [rdx+0x01]
       mov      byte  ptr [rbx+rdx], cl
       mov      qword ptr [rbp+0xA0], rsi
       jmp      SHORT G_M000_IG40

G_M000_IG39:                ;; offset=0x044F
       mov      rdx, rcx
       lea      rcx, [rdx+0x01]
       mov      qword ptr [rbp+0xA0], rcx
       lea      rax, bword ptr [r13+0x10]
       cmp      r14d, dword ptr [rax+0x08]
       jae      G_M000_IG80
       mov      rax, bword ptr [rax]
       mov      r8d, r14d
       mov      r9d, r10d
       add      r9d, dword ptr [rax+4*r8]
       dec      r9d
       mov      byte  ptr [rbx+rdx], r9b

G_M000_IG40:                ;; offset=0x047F
       add      r11d, r10d
       lea      edx, [r11-0x01]
       movsxd   rdx, edx
       mov      r15, bword ptr [rbp+0x10]
       mov      r9d, dword ptr [r15+4*rdx]
       mov      esi, r9d
       mov      edx, esi
       and      edx, 0xFF00FF
       mov      eax, esi
       and      eax, -0xFF0100
       shl      rax, 24
       or       rdx, rax
       mov      rax, 0x300070005000B
       imul     rdx, rax
       shr      rdx, 48
       and      edx, 63
       mov      r14, bword ptr [rbp+0x40]
       mov      dword ptr [r14+4*rdx], esi
       mov      rcx, qword ptr [rbp+0xA0]
       jmp      G_M000_IG73

G_M000_IG41:                ;; offset=0x04D3
       mov      dword ptr [rbp+0x84], eax
       movsxd   rsi, r11d
       mov      r15, bword ptr [rbp+0x10]
       mov      esi, dword ptr [r15+4*rsi]
       mov      r8d, esi
       and      r8d, 0xFF00FF
       mov      r15d, esi
       and      r15d, -0xFF0100
       shl      r15, 24
       or       r8, r15
       mov      r15, 0x300070005000B
       imul     r8, r15
       shr      r8, 48
       and      r8d, 63
       mov      r15d, r8d
       mov      rax, bword ptr [rbp+0x40]
       lea      r15, bword ptr [rax+4*r15]
       cmp      dword ptr [r15], esi
       je       G_M000_IG71
       mov      dword ptr [r15], esi
       mov      r8d, esi
       xor      r8d, r9d
       shr      r8d, 24
       je       G_M000_IG58
       mov      r15d, esi
       shr      r15d, 24
       xor      r8d, r8d
       mov      dword ptr [rbp+0x68], 5
       test     r15d, r15d
       je       G_M000_IG47
       cmp      r15d, 255
       je       SHORT G_M000_IG46
       mov      eax, dword ptr [rbp+0x54]
       cmp      r11d, eax
       jge      SHORT G_M000_IG43

G_M000_IG42:                ;; offset=0x0567
       jmp      SHORT G_M000_IG44

G_M000_IG43:                ;; offset=0x0569
       mov      dword ptr [rbp+0x9C], r11d
       mov      edx, r11d
       sub      edx, eax
       movsxd   rdx, edx
       mov      rax, bword ptr [rbp+0x10]
       mov      edx, dword ptr [rax+4*rdx]
       shr      edx, 24
       cmp      edx, r15d
       mov      r11d, dword ptr [rbp+0x9C]
       je       SHORT G_M000_IG45

G_M000_IG44:                ;; offset=0x058E
       mov      r8d, 3
       mov      dword ptr [rbp+0x6C], 1
       mov      qword ptr [rbp+0xA0], rcx
       mov      dword ptr [rbp+0x9C], r11d
       mov      dword ptr [rbp+0x70], r8d
       mov      dword ptr [rbp+0x98], r10d
       jmp      SHORT G_M000_IG48

G_M000_IG45:                ;; offset=0x05B6
       mov      eax, 2
       mov      qword ptr [rbp+0xA0], rcx
       mov      dword ptr [rbp+0x9C], r11d
       mov      dword ptr [rbp+0x70], eax
       mov      dword ptr [rbp+0x98], r10d
       mov      dword ptr [rbp+0x6C], r8d
       jmp      SHORT G_M000_IG48

G_M000_IG46:                ;; offset=0x05D9
       mov      eax, 1
       mov      qword ptr [rbp+0xA0], rcx
       mov      dword ptr [rbp+0x9C], r11d
       mov      dword ptr [rbp+0x70], eax
       mov      dword ptr [rbp+0x98], r10d
       mov      dword ptr [rbp+0x6C], r8d
       jmp      SHORT G_M000_IG48

G_M000_IG47:                ;; offset=0x05FC
       xor      eax, eax
       mov      dword ptr [rbp+0x70], eax
       mov      qword ptr [rbp+0xA0], rcx
       mov      dword ptr [rbp+0x9C], r11d
       mov      dword ptr [rbp+0x98], r10d
       mov      dword ptr [rbp+0x6C], r8d

G_M000_IG48:                ;; offset=0x061A
       and      r9d, 0xFFFFFF
       mov      edx, r15d
       shl      edx, 24
       or       r9d, edx
       mov      dword ptr [rbp+0x64], r9d
       mov      ecx, esi
       mov      edx, r9d
       call     [Tedd.FIC.QpCodec+Enc`1[Tedd.FIC.BaseW]:ColourCost(uint,uint):int]
       mov      ecx, dword ptr [rbp+0x6C]
       lea      eax, [rax+rcx+0x01]
       cmp      eax, 5
       jl       SHORT G_M000_IG49
       mov      edx, -1
       mov      dword ptr [rbp+0x70], edx
       mov      eax, dword ptr [rbp+0x68]

G_M000_IG49:                ;; offset=0x0650
       cmp      eax, 2
       jle      SHORT G_M000_IG50
       imul     eax, esi, 0xFFFFFFFF9E3779B1
       shr      eax, 22
       mov      r8d, eax
       mov      r10, bword ptr [rbp+0x38]
       lea      r8, bword ptr [r10+4*r8]
       cmp      dword ptr [r8], esi
       je       G_M000_IG57
       mov      dword ptr [r8], esi

G_M000_IG50:                ;; offset=0x0675
       mov      edx, dword ptr [rbp+0x70]
       test     edx, edx
       jge      SHORT G_M000_IG51
       mov      r15, qword ptr [rbp+0xA0]
       mov      byte  ptr [rbx+r15], 255
       mov      dword ptr [rbx+r15+0x01], esi
       add      r15, 5
       mov      rcx, r15
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      G_M000_IG72

G_M000_IG51:                ;; offset=0x06A7
       mov      r8, qword ptr [rbp+0xA0]
       mov      rax, r8
       lea      r8, [rax+0x01]
       add      edx, 250
       mov      byte  ptr [rbx+rax], dl
       cmp      ecx, 1
       jne      SHORT G_M000_IG52
       mov      rcx, r8
       lea      r8, [rcx+0x01]
       mov      byte  ptr [rbx+rcx], r15b

G_M000_IG52:                ;; offset=0x06CE
       mov      r15d, dword ptr [rbp+0x64]
       mov      ecx, esi
       sub      ecx, r15d
       movsx    rcx, cl
       add      ecx, 2
       mov      edx, esi
       shr      edx, 8
       mov      eax, r15d
       shr      eax, 8
       sub      edx, eax
       movsx    rdx, dl
       add      edx, 2
       mov      eax, esi
       shr      eax, 16
       shr      r15d, 16
       sub      eax, r15d
       movsx    rax, al
       add      eax, 2
       mov      r9d, ecx
       or       r9d, edx
       or       r9d, eax
       cmp      r9d, 4
       jb       G_M000_IG55
       lea      r9d, [rdx+0x1E]
       mov      dword ptr [rbp+0x5C], r9d
       mov      r11d, ecx
       sub      r11d, edx
       add      r11d, 8
       mov      r15d, eax
       sub      r15d, edx
       add      r15d, 8
       mov      r10d, r11d
       or       r10d, r15d
       cmp      r10d, 16
       setb     r10b
       movzx    r10, r10b
       cmp      r9d, 64
       setb     r9b
       movzx    r9, r9b
       test     r10d, r9d
       jne      G_M000_IG54
       lea      r11d, [rdx+0x3E]
       sub      ecx, edx
       movsx    rcx, cl
       add      ecx, 32
       sub      eax, edx
       movsx    rdx, al
       add      edx, 32
       mov      eax, ecx
       or       eax, edx
       cmp      eax, 64
       setb     al
       movzx    rax, al
       cmp      r11d, 128
       setb     r10b
       movzx    r10, r10b
       test     eax, r10d
       jne      SHORT G_M000_IG53
       mov      r11d, esi
       and      r11d, 0xFFFFFF
       shl      r11d, 8
       or       r11d, 254
       mov      dword ptr [rbx+r8], r11d
       lea      rax, [r8+0x04]
       jmp      SHORT G_M000_IG56

G_M000_IG53:                ;; offset=0x07B1
       shl      r11d, 12
       shl      ecx, 6
       or       ecx, r11d
       or       ecx, edx
       mov      eax, ecx
       shr      eax, 16
       add      eax, 192
       mov      edx, ecx
       shr      edx, 8
       movzx    rdx, dl
       shl      edx, 8
       or       edx, eax
       movzx    rcx, cl
       shl      ecx, 16
       or       ecx, edx
       mov      dword ptr [rbx+r8], ecx
       lea      rax, [r8+0x03]
       jmp      SHORT G_M000_IG56

G_M000_IG54:                ;; offset=0x07E6
       shl      r11d, 4
       or       r11d, r15d
       shl      r11d, 8
       mov      r9d, dword ptr [rbp+0x5C]
       or       r9d, 128
       or       r9d, r11d
       mov      word  ptr [rbx+r8], r9w
       lea      rax, [r8+0x02]
       jmp      SHORT G_M000_IG56

G_M000_IG55:                ;; offset=0x080A
       shl      ecx, 4
       or       ecx, 64
       shl      edx, 2
       or       ecx, edx
       or       ecx, eax
       mov      byte  ptr [rbx+r8], cl
       lea      rax, [r8+0x01]

G_M000_IG56:                ;; offset=0x081F
       mov      r8, rax
       mov      rcx, r8
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      G_M000_IG72

G_M000_IG57:                ;; offset=0x0838
       mov      ecx, eax
       sar      ecx, 8
       add      ecx, 246
       mov      r8, qword ptr [rbp+0xA0]
       mov      byte  ptr [rbx+r8], cl
       mov      byte  ptr [rbx+r8+0x01], al
       add      r8, 2
       mov      qword ptr [rbp+0xA0], r8
       mov      rcx, qword ptr [rbp+0xA0]
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      G_M000_IG72

G_M000_IG58:                ;; offset=0x0878
       mov      qword ptr [rbp+0xA0], rcx
       mov      dword ptr [rbp+0x9C], r11d
       mov      dword ptr [rbp+0x98], r10d
       mov      ecx, esi
       mov      dword ptr [rbp+0xAC], r9d
       mov      edx, r9d
       call     [Tedd.FIC.QpCodec+Enc`1[Tedd.FIC.BaseW]:ColourCost(uint,uint):int]
       cmp      eax, 2
       jg       G_M000_IG64
       mov      r15d, dword ptr [rbp+0xAC]
       mov      eax, esi
       sub      eax, r15d
       movsx    rax, al
       add      eax, 2
       mov      r9d, esi
       shr      r9d, 8
       mov      ecx, r15d
       shr      ecx, 8
       sub      r9d, ecx
       movsx    r9, r9b
       add      r9d, 2
       mov      ecx, esi
       shr      ecx, 16
       mov      edx, r15d
       shr      edx, 16
       sub      ecx, edx
       movsx    rcx, cl
       add      ecx, 2
       mov      edx, eax
       or       edx, r9d
       or       edx, ecx
       cmp      edx, 4
       jb       G_M000_IG62
       lea      edx, [r9+0x1E]
       mov      r8d, eax
       sub      r8d, r9d
       add      r8d, 8
       mov      r10d, ecx
       sub      r10d, r9d
       add      r10d, 8
       mov      r11d, r8d
       or       r11d, r10d
       cmp      r11d, 16
       setb     r11b
       movzx    r11, r11b
       cmp      edx, 64
       setb     r15b
       movzx    r15, r15b
       test     r11d, r15d
       jne      G_M000_IG61
       lea      r8d, [r9+0x3E]
       sub      eax, r9d
       movsx    r10, al
       add      r10d, 32
       sub      ecx, r9d
       movsx    rdx, cl
       add      edx, 32
       mov      r9d, r10d
       or       r9d, edx
       cmp      r9d, 64
       setb     al
       movzx    rax, al
       cmp      r8d, 128
       setb     r9b
       movzx    r9, r9b

G_M000_IG59:                ;; offset=0x096D
       test     eax, r9d
       jne      SHORT G_M000_IG60
       mov      r8d, esi
       and      r8d, 0xFFFFFF
       shl      r8d, 8
       or       r8d, 254
       mov      r15, qword ptr [rbp+0xA0]
       mov      dword ptr [rbx+r15], r8d
       lea      rax, [r15+0x04]
       jmp      G_M000_IG63

G_M000_IG60:                ;; offset=0x099B
       shl      r8d, 12
       shl      r10d, 6
       or       r8d, r10d
       or       edx, r8d
       mov      eax, edx
       shr      eax, 16
       add      eax, 192
       mov      r9d, edx
       shr      r9d, 8
       movzx    r9, r9b
       shl      r9d, 8
       or       eax, r9d
       movzx    r9, dl
       shl      r9d, 16
       or       eax, r9d
       mov      r15, qword ptr [rbp+0xA0]
       mov      dword ptr [rbx+r15], eax
       lea      rax, [r15+0x03]
       jmp      SHORT G_M000_IG63

G_M000_IG61:                ;; offset=0x09E1
       shl      r8d, 4
       or       r8d, r10d
       shl      r8d, 8
       or       edx, 128
       or       edx, r8d
       mov      r15, qword ptr [rbp+0xA0]
       mov      word  ptr [rbx+r15], dx
       lea      rax, [r15+0x02]
       jmp      SHORT G_M000_IG63

G_M000_IG62:                ;; offset=0x0A07
       shl      eax, 4
       or       eax, 64
       shl      r9d, 2
       or       eax, r9d
       or       eax, ecx
       mov      r15, qword ptr [rbp+0xA0]
       mov      byte  ptr [rbx+r15], al
       lea      rax, [r15+0x01]

G_M000_IG63:                ;; offset=0x0A25
       mov      r15, rax
       mov      rcx, r15
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      G_M000_IG72

G_M000_IG64:                ;; offset=0x0A3E
       imul     eax, esi, 0xFFFFFFFF9E3779B1
       shr      eax, 22
       mov      r9d, eax
       mov      r10, bword ptr [rbp+0x38]
       lea      r9, bword ptr [r10+4*r9]
       mov      bword ptr [rbp+0x30], r9
       cmp      dword ptr [r9], esi
       je       G_M000_IG70
       mov      ecx, dword ptr [rbp+0xAC]
       mov      eax, esi
       sub      eax, ecx
       movsx    rax, al
       add      eax, 2
       mov      edx, esi
       shr      edx, 8
       mov      r8d, ecx
       shr      r8d, 8
       sub      edx, r8d
       movsx    rdx, dl
       add      edx, 2
       mov      r8d, esi
       shr      r8d, 16
       shr      ecx, 16
       sub      r8d, ecx
       movsx    rcx, r8b
       add      ecx, 2
       mov      r8d, eax
       or       r8d, edx
       or       r8d, ecx
       cmp      r8d, 4
       jb       G_M000_IG68
       lea      r8d, [rdx+0x1E]
       mov      dword ptr [rbp+0x60], r8d
       mov      r11d, eax
       sub      r11d, edx
       add      r11d, 8
       mov      r10d, ecx
       sub      r10d, edx
       add      r10d, 8
       mov      r9d, r11d
       or       r9d, r10d
       cmp      r9d, 16
       setb     r9b
       movzx    r9, r9b
       cmp      r8d, 64
       setb     r8b
       movzx    r8, r8b
       test     r9d, r8d
       jne      G_M000_IG67
       lea      r11d, [rdx+0x3E]
       sub      eax, edx
       movsx    r10, al
       add      r10d, 32
       sub      ecx, edx
       movsx    rdx, cl
       add      edx, 32
       mov      eax, r10d
       or       eax, edx
       cmp      eax, 64
       setb     al
       movzx    rax, al
       cmp      r11d, 128
       setb     r9b
       movzx    r9, r9b
       test     eax, r9d

G_M000_IG65:                ;; offset=0x0B27
       jne      SHORT G_M000_IG66
       mov      r11d, esi
       and      r11d, 0xFFFFFF
       shl      r11d, 8
       or       r11d, 254
       mov      r15, qword ptr [rbp+0xA0]
       mov      dword ptr [rbx+r15], r11d
       lea      rax, [r15+0x04]
       jmp      G_M000_IG69

G_M000_IG66:                ;; offset=0x0B52
       shl      r11d, 12
       shl      r10d, 6
       or       r10d, r11d
       or       edx, r10d
       mov      eax, edx
       shr      eax, 16
       add      eax, 192
       mov      r9d, edx
       shr      r9d, 8
       movzx    r9, r9b
       shl      r9d, 8
       or       eax, r9d
       movzx    r9, dl
       shl      r9d, 16
       or       eax, r9d
       mov      r15, qword ptr [rbp+0xA0]
       mov      dword ptr [rbx+r15], eax
       lea      rax, [r15+0x03]
       jmp      SHORT G_M000_IG69

G_M000_IG67:                ;; offset=0x0B98
       shl      r11d, 4
       or       r10d, r11d
       shl      r10d, 8
       mov      r8d, dword ptr [rbp+0x60]
       or       r8d, 128
       or       r8d, r10d
       mov      r15, qword ptr [rbp+0xA0]
       mov      word  ptr [rbx+r15], r8w
       lea      rax, [r15+0x02]
       jmp      SHORT G_M000_IG69

G_M000_IG68:                ;; offset=0x0BC3
       shl      eax, 4
       or       eax, 64
       shl      edx, 2
       or       eax, edx
       or       eax, ecx
       mov      r15, qword ptr [rbp+0xA0]
       mov      byte  ptr [rbx+r15], al
       lea      rax, [r15+0x01]

G_M000_IG69:                ;; offset=0x0BDF
       mov      r15, rax
       mov      r9, bword ptr [rbp+0x30]
       mov      dword ptr [r9], esi
       mov      rcx, r15
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      SHORT G_M000_IG72

G_M000_IG70:                ;; offset=0x0BFC
       mov      r9d, eax
       sar      r9d, 8
       add      r9d, 246
       mov      r15, qword ptr [rbp+0xA0]
       mov      byte  ptr [rbx+r15], r9b
       mov      byte  ptr [rbx+r15+0x01], al
       add      r15, 2
       mov      rcx, r15
       mov      r10d, dword ptr [rbp+0x98]
       mov      r11d, dword ptr [rbp+0x9C]
       jmp      SHORT G_M000_IG72

G_M000_IG71:                ;; offset=0x0C31
       mov      r9, rcx
       lea      rcx, [r9+0x01]
       mov      byte  ptr [rbx+r9], r8b

G_M000_IG72:                ;; offset=0x0C3C
       inc      r11d
       mov      dword ptr [rbp+0x98], r10d
       mov      r15, bword ptr [rbp+0x10]

G_M000_IG73:                ;; offset=0x0C4A
       mov      edi, dword ptr [rbp+0x58]
       cmp      r11d, edi
       mov      r9d, esi
       mov      eax, dword ptr [rbp+0x84]
       mov      r14, bword ptr [rbp+0x08]
       jl       G_M000_IG07

G_M000_IG74:                ;; offset=0x0C63
       mov      rax, rcx
       mov      r9, 0x48DAEBEF2F2B
       cmp      qword ptr [rbp+0xB0], r9
       je       SHORT G_M000_IG75
       call     CORINFO_HELP_FAIL_FAST

G_M000_IG75:                ;; offset=0x0C7E
       nop

G_M000_IG76:                ;; offset=0x0C7F
       lea      rsp, [rbp+0xB8]
       pop      rbx
       pop      rsi
       pop      rdi
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret

G_M000_IG77:                ;; offset=0x0C93
       mov      rax, -1
       mov      r9, 0x48DAEBEF2F2B
       cmp      qword ptr [rbp+0xB0], r9
       je       SHORT G_M000_IG78
       call     CORINFO_HELP_FAIL_FAST

G_M000_IG78:                ;; offset=0x0CB5
       nop

G_M000_IG79:                ;; offset=0x0CB6
       lea      rsp, [rbp+0xB8]
       pop      rbx
       pop      rsi
       pop      rdi
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret

G_M000_IG80:                ;; offset=0x0CCA
       call     CORINFO_HELP_RNGCHKFAIL
       int3

RWD00  	dq	4090000000000000h	;         1024
RWD08  	dq	43E0000000000000h

; Total bytes of code 3280
