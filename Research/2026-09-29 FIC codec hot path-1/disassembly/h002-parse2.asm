; Assembly listing for method Tedd.FIC.QpCodec+Enc`1[Tedd.FIC.BaseW]:Parse2(byref,byref,byref,byref,int,int,byref):nint (FullOpts)
; Emitting BLENDED_CODE for x64 + VEX on Windows
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 6 single block inlinees; 9 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     r12
       push     rdi
       push     rsi
       push     rbx
       sub      rsp, 200
       lea      rbp, [rsp+0x20]
       mov      rax, 0x4A607C0744FB
       mov      qword ptr [rbp+0xA0], rax

G_M000_IG02:                ;; offset=0x0029
       mov      rbx, bword ptr [rbp+0x120]
       mov      esi, dword ptr [rbp+0x118]
       mov      dword ptr [rbp+0x3C], esi
       mov      edi, dword ptr [rbp+0x110]
       mov      dword ptr [rbp+0x40], edi
       mov      r14, r9
       mov      bword ptr [rbp], r14
       mov      bword ptr [rbp+0x08], rdx
       mov      r13, rcx
       mov      bword ptr [rbp+0x10], r13
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
       mov      qword ptr [rbp+0x30], rcx
       mov      edx, 0x1100
       call     CORINFO_HELP_MEMZERO
       mov      rcx, qword ptr [rbp+0x30]
       mov      rax, rcx
       mov      bword ptr [rbp+0x18], rax
       mov      rcx, rax
       mov      edx, 0x1100
       call     [System.SpanHelpers:ClearWithoutReferences(byref,nuint)]
       mov      r8, bword ptr [rbp+0x18]
       mov      bword ptr [rbp+0x28], r8
       lea      r10, bword ptr [r8+0x100]
       mov      bword ptr [rbp+0x20], r10
       mov      r9d, 0xFFFFFFFFFF000000
       xor      ecx, ecx
       xor      r11d, r11d
       mov      dword ptr [rbp+0x90], -1
       xor      eax, eax
       mov      qword ptr [rbp+0x88], rax
       mov      r10, qword ptr [r13+0x58]
       mov      qword ptr [rbp+0x80], r10
       mov      r10, 0x7FFFFFFFFFFFFFFF
       cmp      qword ptr [rbp+0x80], r10
       jne      SHORT G_M000_IG05

G_M000_IG04:                ;; offset=0x00F6
       mov      r10d, 0x7FFFFFFF
       jmp      SHORT G_M000_IG06
       align    [4 bytes for IG13]

G_M000_IG05:                ;; offset=0x0102
       mov      eax, edi
       cdq
       idiv     edx:eax, dword ptr [(reloc 0x7ffc571bb1b8)]
       mov      r10d, eax

G_M000_IG06:                ;; offset=0x010E
       mov      eax, r10d
       vmovsd   xmm0, qword ptr [(reloc 0x7ffc571bb1b0)]
       vmulsd   xmm0, xmm0, qword ptr [reloc @RWD00]
       vcmpordsd xmm1, xmm0, xmm0
       vandpd   xmm1, xmm1, xmm0
       mov      r10, 0x7FFFFFFFFFFFFFFF
       vcvttsd2si rdx, xmm1
       vucomisd xmm0, qword ptr [reloc @RWD08]
       cmovb    r10, rdx
       mov      qword ptr [rbp+0x70], r10
       cmp      r11d, edi
       jge      G_M000_IG71

G_M000_IG07:                ;; offset=0x0152
       mov      edx, r11d
       sar      edx, 6
       mov      r10d, dword ptr [rbp+0x90]
       cmp      edx, r10d
       je       SHORT G_M000_IG11

G_M000_IG08:                ;; offset=0x0164
       mov      r10d, edx
       mov      dword ptr [rbp+0x90], r10d
       mov      edx, r10d
       imul     edx, r12d
       lea      edx, [rdx+r12-0x01]
       movsxd   rdx, edx
       mov      rdx, qword ptr [r14+8*rdx]
       cmp      r11d, eax
       jge      SHORT G_M000_IG10

G_M000_IG09:                ;; offset=0x0186
       mov      qword ptr [rbp+0x88], rdx
       mov      r10d, dword ptr [rbp+0x90]
       jmp      SHORT G_M000_IG11

G_M000_IG10:                ;; offset=0x0196
       movsxd   rax, edi
       imul     rax, rcx
       shl      rax, 10
       mov      rsi, qword ptr [rbp+0x80]
       imul     rsi, qword ptr [rbp+0x70]
       movsxd   r8, r11d
       imul     r8, rsi
       cmp      rax, r8
       jg       G_M000_IG74
       mov      eax, edi
       sar      eax, 3
       add      eax, r11d
       mov      dword ptr [rbp+0x7C], eax
       mov      qword ptr [rbp+0x88], rdx
       mov      eax, dword ptr [rbp+0x7C]
       mov      r10d, dword ptr [rbp+0x90]

G_M000_IG11:                ;; offset=0x01D9
       mov      rdx, qword ptr [rbp+0x88]
       bt       rdx, r11
       jae      G_M000_IG44

G_M000_IG12:                ;; offset=0x01EA
       mov      dword ptr [rbp+0x7C], eax
       mov      r9, bword ptr [r13+0x40]
       mov      esi, dword ptr [r13+0x48]
       mov      r8d, r11d
       and      r8d, 63
       mov      dword ptr [rbp+0x6C], r8d
       mov      eax, r11d
       sar      eax, 6
       imul     eax, r12d
       cdqe
       lea      rax, bword ptr [r14+8*rax]
       xor      edx, edx
       xor      r15d, r15d
       test     esi, esi
       mov      dword ptr [rbp+0x90], r10d
       je       SHORT G_M000_IG17

G_M000_IG13:                ;; offset=0x0220
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

G_M000_IG14:                ;; offset=0x0248
       jmp      SHORT G_M000_IG16

G_M000_IG15:                ;; offset=0x024A
       mov      edx, r10d

G_M000_IG16:                ;; offset=0x024D
       inc      r15d
       cmp      r15d, esi
       jl       SHORT G_M000_IG13

G_M000_IG17:                ;; offset=0x0255
       mov      r10d, edx
       sar      r10d, 4
       mov      r14d, r8d
       neg      r14d
       add      r14d, 64
       mov      dword ptr [rbp+0x44], r14d
       cmp      r14d, r10d
       jg       G_M000_IG36
       xor      r10d, r10d
       xor      r15d, r15d
       mov      dword ptr [rbp+0x68], r15d
       test     esi, esi
       jne      SHORT G_M000_IG19

G_M000_IG18:                ;; offset=0x0281
       jmp      G_M000_IG37
       align    [6 bytes for IG34]

G_M000_IG19:                ;; offset=0x028C
       xor      edx, edx
       jmp      SHORT G_M000_IG31

G_M000_IG20:                ;; offset=0x0290
       mov      rdi, r13
       not      rdi
       tzcnt    rdi, rdi
       add      edi, r15d
       mov      r15d, edi
       cmp      r15d, r8d
       jg       SHORT G_M000_IG21
       mov      edi, r15d
       jmp      SHORT G_M000_IG22

G_M000_IG21:                ;; offset=0x02AB
       mov      edi, r8d

G_M000_IG22:                ;; offset=0x02AE
       jmp      SHORT G_M000_IG26

G_M000_IG23:                ;; offset=0x02B0
       cmp      r15d, r8d
       jg       SHORT G_M000_IG24
       jmp      SHORT G_M000_IG25

G_M000_IG24:                ;; offset=0x02B7
       mov      r15d, r8d

G_M000_IG25:                ;; offset=0x02BA
       mov      edi, r15d

G_M000_IG26:                ;; offset=0x02BD
       cmp      edi, r10d
       jg       SHORT G_M000_IG28

G_M000_IG27:                ;; offset=0x02C2
       mov      r13, bword ptr [rbp+0x10]
       jmp      SHORT G_M000_IG29

G_M000_IG28:                ;; offset=0x02C8
       mov      r10d, edi
       mov      r15d, r14d
       mov      dword ptr [rbp+0x68], r15d
       mov      r13, bword ptr [rbp+0x10]

G_M000_IG29:                ;; offset=0x02D6
       add      rdx, 4
       dec      esi
       je       G_M000_IG37

G_M000_IG30:                ;; offset=0x02E2
       mov      edi, dword ptr [rbp+0x40]

G_M000_IG31:                ;; offset=0x02E5
       mov      r14d, dword ptr [r9+rdx]
       lea      r8d, [r11+r10]
       cmp      r8d, edi
       jge      SHORT G_M000_IG29

G_M000_IG32:                ;; offset=0x02F2
       mov      r15d, r8d
       sar      r15d, 6
       imul     r15d, r12d
       add      r15d, r14d
       movsxd   r15, r15d
       mov      rdi, bword ptr [rbp]
       mov      r15, qword ptr [rdi+8*r15]
       bt       r15, r8
       jae      SHORT G_M000_IG29
       cmp      r14d, dword ptr [r13+0x08]
       jae      G_M000_IG77
       mov      r8, bword ptr [r13]
       mov      r15d, r14d
       mov      r8d, dword ptr [r8+4*r15]
       mov      r15d, r14d
       mov      edi, dword ptr [rbp+0x6C]
       shrx     r15, qword ptr [rax+8*r15], rdi
       not      r15
       tzcnt    r15, r15
       cmp      dword ptr [rbp+0x44], r15d
       jge      SHORT G_M000_IG33
       mov      r15d, dword ptr [rbp+0x44]
       mov      edi, r15d
       mov      r15d, edi

G_M000_IG33:                ;; offset=0x034A
       cmp      dword ptr [rbp+0x44], r15d
       jg       G_M000_IG23
       cmp      r15d, r8d
       jge      G_M000_IG23
       mov      edi, r14d

G_M000_IG34:                ;; offset=0x0360
       movsxd   r13, r12d
       add      rdi, r13
       mov      r13, qword ptr [rax+8*rdi]
       cmp      r13, -1
       jne      G_M000_IG20
       add      r15d, 64
       cmp      r15d, r8d
       jl       SHORT G_M000_IG34

G_M000_IG35:                ;; offset=0x037D
       mov      edi, r8d
       jmp      G_M000_IG26

G_M000_IG36:                ;; offset=0x0385
       and      edx, 15
       neg      edx
       add      edx, 15
       cmp      edx, esi
       jae      G_M000_IG77
       mov      r15d, dword ptr [r9+4*rdx]
       mov      dword ptr [rbp+0x68], r15d

G_M000_IG37:                ;; offset=0x039D
       mov      r15d, dword ptr [rbp+0x68]
       cmp      r15d, dword ptr [r13+0x08]
       jae      G_M000_IG77
       mov      rdx, bword ptr [r13]
       mov      eax, r15d
       mov      edx, dword ptr [rdx+4*rax]
       cmp      r10d, edx
       jg       SHORT G_M000_IG38
       jmp      SHORT G_M000_IG39
       align    [3 bytes for IG40]

G_M000_IG38:                ;; offset=0x03BF
       mov      r10d, edx

G_M000_IG39:                ;; offset=0x03C2
       lea      rdx, bword ptr [r13+0x20]
       cmp      r15d, dword ptr [rdx+0x08]
       jae      G_M000_IG77
       mov      rdx, bword ptr [rdx]
       mov      eax, r15d
       mov      edx, dword ptr [rdx+4*rax]
       cmp      r10d, edx
       jle      SHORT G_M000_IG42
       lea      rax, [rcx+0x01]
       mov      rsi, rax
       lea      rax, bword ptr [r13+0x30]
       cmp      r15d, dword ptr [rax+0x08]
       jae      G_M000_IG77
       mov      rax, bword ptr [rax]
       mov      r8d, r15d
       movzx    rax, byte  ptr [rax+4*r8]
       mov      byte  ptr [rbx+rcx], al
       mov      ecx, r10d
       sub      ecx, edx
       dec      ecx
       cmp      ecx, 128
       jl       SHORT G_M000_IG41

G_M000_IG40:                ;; offset=0x0410
       mov      rdx, rsi
       lea      rsi, [rdx+0x01]
       mov      eax, ecx
       or       eax, 128
       mov      byte  ptr [rbx+rdx], al
       sar      ecx, 7
       cmp      ecx, 128
       jge      SHORT G_M000_IG40

G_M000_IG41:                ;; offset=0x042C
       mov      rdx, rsi
       lea      rsi, [rdx+0x01]
       mov      byte  ptr [rbx+rdx], cl
       mov      qword ptr [rbp+0x98], rsi
       jmp      SHORT G_M000_IG43

G_M000_IG42:                ;; offset=0x043F
       mov      rdx, rcx
       lea      rcx, [rdx+0x01]
       mov      qword ptr [rbp+0x98], rcx
       lea      rax, bword ptr [r13+0x10]
       cmp      r15d, dword ptr [rax+0x08]
       jae      G_M000_IG77
       mov      rax, bword ptr [rax]
       mov      r8d, r15d
       mov      r9d, r10d
       add      r9d, dword ptr [rax+4*r8]
       dec      r9d
       mov      byte  ptr [rbx+rdx], r9b

G_M000_IG43:                ;; offset=0x046F
       add      r11d, r10d
       lea      edx, [r11-0x01]
       movsxd   rdx, edx
       mov      r15, bword ptr [rbp+0x08]
       mov      r9d, dword ptr [r15+4*rdx]
       mov      edx, r9d
       and      edx, 0xFF00FF
       mov      eax, r9d
       and      eax, -0xFF0100
       shl      rax, 24
       or       rdx, rax
       mov      rax, 0x300070005000B
       imul     rdx, rax
       shr      rdx, 48
       and      edx, 63
       mov      rsi, bword ptr [rbp+0x28]
       mov      dword ptr [rsi+4*rdx], r9d
       mov      rcx, qword ptr [rbp+0x98]
       jmp      G_M000_IG70

G_M000_IG44:                ;; offset=0x04C2
       mov      dword ptr [rbp+0x7C], eax
       movsxd   rsi, r11d
       mov      r15, bword ptr [rbp+0x08]
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
       mov      rax, bword ptr [rbp+0x28]
       lea      r15, bword ptr [rax+4*r15]
       cmp      dword ptr [r15], esi
       je       G_M000_IG68
       mov      dword ptr [r15], esi
       mov      r8d, esi
       xor      r8d, r9d
       shr      r8d, 24
       je       G_M000_IG61
       mov      r15d, esi
       shr      r15d, 24
       xor      r8d, r8d
       mov      dword ptr [rbp+0x5C], 5
       test     r15d, r15d
       je       G_M000_IG50
       cmp      r15d, 255
       je       SHORT G_M000_IG49
       mov      eax, dword ptr [rbp+0x3C]
       cmp      r11d, eax
       jge      SHORT G_M000_IG46

G_M000_IG45:                ;; offset=0x0553
       jmp      SHORT G_M000_IG47

G_M000_IG46:                ;; offset=0x0555
       mov      dword ptr [rbp+0x94], r11d
       mov      edx, r11d
       sub      edx, eax
       movsxd   rdx, edx
       mov      rax, bword ptr [rbp+0x08]
       mov      edx, dword ptr [rax+4*rdx]
       shr      edx, 24
       cmp      edx, r15d
       mov      r11d, dword ptr [rbp+0x94]
       je       SHORT G_M000_IG48

G_M000_IG47:                ;; offset=0x057A
       mov      r8d, 3
       mov      dword ptr [rbp+0x60], 1
       mov      qword ptr [rbp+0x98], rcx
       mov      dword ptr [rbp+0x94], r11d
       mov      dword ptr [rbp+0x64], r8d
       mov      dword ptr [rbp+0x90], r10d
       jmp      SHORT G_M000_IG51

G_M000_IG48:                ;; offset=0x05A2
       mov      eax, 2
       mov      qword ptr [rbp+0x98], rcx
       mov      dword ptr [rbp+0x94], r11d
       mov      dword ptr [rbp+0x64], eax
       mov      dword ptr [rbp+0x90], r10d
       mov      dword ptr [rbp+0x60], r8d
       jmp      SHORT G_M000_IG51

G_M000_IG49:                ;; offset=0x05C5
       mov      eax, 1
       mov      qword ptr [rbp+0x98], rcx
       mov      dword ptr [rbp+0x94], r11d
       mov      dword ptr [rbp+0x64], eax
       mov      dword ptr [rbp+0x90], r10d
       mov      dword ptr [rbp+0x60], r8d
       jmp      SHORT G_M000_IG51

G_M000_IG50:                ;; offset=0x05E8
       xor      eax, eax
       mov      dword ptr [rbp+0x64], eax
       mov      qword ptr [rbp+0x98], rcx
       mov      dword ptr [rbp+0x94], r11d
       mov      dword ptr [rbp+0x90], r10d
       mov      dword ptr [rbp+0x60], r8d

G_M000_IG51:                ;; offset=0x0606
       and      r9d, 0xFFFFFF
       mov      edx, r15d
       shl      edx, 24
       or       r9d, edx
       mov      dword ptr [rbp+0x58], r9d
       mov      ecx, esi
       mov      edx, r9d
       call     [Tedd.FIC.QpCodec+Enc`1[Tedd.FIC.BaseW]:ColourCost(uint,uint):int]
       mov      r9d, dword ptr [rbp+0x60]
       lea      eax, [rax+r9+0x01]
       cmp      eax, 5
       jl       SHORT G_M000_IG52
       mov      ecx, -1
       mov      dword ptr [rbp+0x64], ecx
       mov      eax, dword ptr [rbp+0x5C]

G_M000_IG52:                ;; offset=0x063E
       cmp      eax, 2
       jle      SHORT G_M000_IG53
       imul     eax, esi, 0xFFFFFFFF9E3779B1
       shr      eax, 22
       mov      edx, eax
       mov      r10, bword ptr [rbp+0x20]
       lea      rdx, bword ptr [r10+4*rdx]
       cmp      dword ptr [rdx], esi
       je       G_M000_IG60
       mov      dword ptr [rdx], esi

G_M000_IG53:                ;; offset=0x0660
       mov      ecx, dword ptr [rbp+0x64]
       test     ecx, ecx
       jge      SHORT G_M000_IG54
       mov      r15, qword ptr [rbp+0x98]
       mov      byte  ptr [rbx+r15], 255
       mov      dword ptr [rbx+r15+0x01], esi
       add      r15, 5
       mov      rcx, r15
       mov      r10d, dword ptr [rbp+0x90]
       mov      r11d, dword ptr [rbp+0x94]
       jmp      G_M000_IG69

G_M000_IG54:                ;; offset=0x0692
       mov      rdx, qword ptr [rbp+0x98]
       mov      rax, rdx
       lea      rdx, [rax+0x01]
       add      ecx, 250
       mov      byte  ptr [rbx+rax], cl
       cmp      r9d, 1
       jne      SHORT G_M000_IG55
       mov      rax, rdx
       lea      rdx, [rax+0x01]
       mov      byte  ptr [rbx+rax], r15b

G_M000_IG55:                ;; offset=0x06BA
       mov      r15d, dword ptr [rbp+0x58]
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
       shr      r15d, 16
       sub      ecx, r15d
       movsx    rcx, cl
       add      ecx, 2
       mov      r8d, eax
       or       r8d, r9d
       or       r8d, ecx
       cmp      r8d, 4
       jb       G_M000_IG58
       lea      r8d, [r9+0x1E]
       mov      dword ptr [rbp+0x48], r8d
       mov      r11d, eax
       sub      r11d, r9d
       add      r11d, 8
       mov      r15d, ecx
       sub      r15d, r9d
       add      r15d, 8
       mov      r10d, r11d
       or       r10d, r15d
       cmp      r10d, 16
       setb     r10b
       movzx    r10, r10b
       cmp      r8d, 64
       setb     r8b
       movzx    r8, r8b
       test     r10d, r8d
       jne      G_M000_IG57
       lea      r11d, [r9+0x3E]
       sub      eax, r9d
       movsx    rax, al
       add      eax, 32
       sub      ecx, r9d
       movsx    r9, cl
       add      r9d, 32
       mov      ecx, eax
       or       ecx, r9d
       cmp      ecx, 64
       setb     cl
       movzx    rcx, cl
       cmp      r11d, 128
       setb     r8b
       movzx    r8, r8b
       test     ecx, r8d
       jne      SHORT G_M000_IG56
       mov      r11d, esi
       and      r11d, 0xFFFFFF
       shl      r11d, 8
       or       r11d, 254
       mov      dword ptr [rbx+rdx], r11d
       lea      rcx, [rdx+0x04]
       jmp      SHORT G_M000_IG59

G_M000_IG56:                ;; offset=0x07A5
       shl      r11d, 12
       shl      eax, 6
       or       eax, r11d
       or       eax, r9d
       mov      ecx, eax
       shr      ecx, 16
       add      ecx, 192
       mov      r9d, eax
       shr      r9d, 8
       movzx    r9, r9b
       shl      r9d, 8
       or       r9d, ecx
       movzx    rax, al
       shl      eax, 16
       or       eax, r9d
       mov      dword ptr [rbx+rdx], eax
       lea      rcx, [rdx+0x03]
       jmp      SHORT G_M000_IG59

G_M000_IG57:                ;; offset=0x07E1
       shl      r11d, 4
       or       r11d, r15d
       shl      r11d, 8
       mov      r8d, dword ptr [rbp+0x48]
       or       r8d, 128
       or       r8d, r11d
       mov      word  ptr [rbx+rdx], r8w
       lea      rcx, [rdx+0x02]
       jmp      SHORT G_M000_IG59

G_M000_IG58:                ;; offset=0x0805
       shl      eax, 4
       or       eax, 64
       shl      r9d, 2
       or       eax, r9d
       or       eax, ecx
       mov      byte  ptr [rbx+rdx], al
       lea      rcx, [rdx+0x01]

G_M000_IG59:                ;; offset=0x081B
       mov      rdx, rcx
       mov      r10d, dword ptr [rbp+0x90]
       mov      r11d, dword ptr [rbp+0x94]
       jmp      G_M000_IG69

G_M000_IG60:                ;; offset=0x0831
       mov      r9d, eax
       sar      r9d, 8
       add      r9d, 246
       mov      rdx, qword ptr [rbp+0x98]
       mov      byte  ptr [rbx+rdx], r9b
       mov      byte  ptr [rbx+rdx+0x01], al
       add      rdx, 2
       mov      rcx, rdx
       mov      r10d, dword ptr [rbp+0x90]
       mov      r11d, dword ptr [rbp+0x94]
       jmp      G_M000_IG69

G_M000_IG61:                ;; offset=0x0868
       mov      r8d, esi
       sub      r8d, r9d
       movsx    r8, r8b
       add      r8d, 2
       mov      r15d, esi
       shr      r15d, 8
       mov      eax, r9d
       shr      eax, 8
       sub      r15d, eax
       movsx    rax, r15b
       add      eax, 2
       mov      r15d, esi
       shr      r15d, 16
       shr      r9d, 16
       sub      r15d, r9d
       movsx    r9, r15b
       add      r9d, 2
       mov      r15d, r8d
       or       r15d, eax
       or       r15d, r9d
       cmp      r15d, 4
       jb       G_M000_IG64
       lea      r15d, [rax+0x1E]
       mov      dword ptr [rbp+0x54], r15d
       mov      edx, r8d
       sub      edx, eax
       add      edx, 8
       mov      dword ptr [rbp+0x50], edx
       mov      r15d, r9d
       sub      r15d, eax
       add      r15d, 8
       mov      dword ptr [rbp+0x4C], r15d
       or       r15d, edx
       cmp      r15d, 16
       setb     r15b
       movzx    r15, r15b
       cmp      dword ptr [rbp+0x54], 64
       setb     dl
       movzx    rdx, dl
       test     r15d, edx
       jne      G_M000_IG63
       lea      edx, [rax+0x3E]
       sub      r8d, eax
       movsx    r8, r8b
       add      r8d, 32
       sub      r9d, eax
       movsx    rax, r9b
       add      eax, 32
       mov      r9d, r8d
       or       r9d, eax
       cmp      r9d, 64
       setb     r9b
       movzx    r9, r9b
       cmp      edx, 128
       setb     r15b
       movzx    r15, r15b
       test     r9d, r15d
       jne      SHORT G_M000_IG62
       mov      edx, esi
       and      edx, 0xFFFFFF
       shl      edx, 8
       or       edx, 254
       mov      dword ptr [rbx+rcx], edx
       lea      r9, [rcx+0x04]
       jmp      SHORT G_M000_IG65

G_M000_IG62:                ;; offset=0x0950
       shl      edx, 12
       shl      r8d, 6
       or       edx, r8d
       or       eax, edx
       mov      r9d, eax
       shr      r9d, 16
       add      r9d, 192
       mov      edx, eax
       shr      edx, 8
       movzx    rdx, dl
       shl      edx, 8
       or       r9d, edx
       movzx    rax, al
       shl      eax, 16
       or       eax, r9d
       mov      dword ptr [rbx+rcx], eax
       lea      r9, [rcx+0x03]
       jmp      SHORT G_M000_IG65

G_M000_IG63:                ;; offset=0x098A
       mov      edx, dword ptr [rbp+0x50]
       shl      edx, 4
       or       edx, dword ptr [rbp+0x4C]
       shl      edx, 8
       mov      r15d, dword ptr [rbp+0x54]
       or       r15d, 128
       or       edx, r15d
       mov      word  ptr [rbx+rcx], dx
       lea      r9, [rcx+0x02]
       jmp      SHORT G_M000_IG65

G_M000_IG64:                ;; offset=0x09AE
       shl      r8d, 4
       or       r8d, 64
       shl      eax, 2
       or       eax, r8d
       or       eax, r9d
       mov      byte  ptr [rbx+rcx], al
       lea      r9, [rcx+0x01]

G_M000_IG65:                ;; offset=0x09C6
       mov      rax, r9
       sub      rax, rcx
       cmp      rax, 2
       jg       SHORT G_M000_IG66
       mov      rcx, r9
       jmp      SHORT G_M000_IG69

G_M000_IG66:                ;; offset=0x09D7
       imul     eax, esi, 0xFFFFFFFF9E3779B1
       shr      eax, 22
       mov      r8d, eax
       mov      r15, bword ptr [rbp+0x20]
       lea      r8, bword ptr [r15+4*r8]
       cmp      dword ptr [r8], esi
       je       SHORT G_M000_IG67
       mov      rcx, r9
       mov      dword ptr [r8], esi
       jmp      SHORT G_M000_IG69

G_M000_IG67:                ;; offset=0x09F8
       mov      r9d, eax
       sar      r9d, 8
       add      r9d, 246
       mov      byte  ptr [rbx+rcx], r9b
       mov      byte  ptr [rbx+rcx+0x01], al
       add      rcx, 2
       jmp      SHORT G_M000_IG69

G_M000_IG68:                ;; offset=0x0A14
       mov      r9, rcx
       lea      rcx, [r9+0x01]
       mov      byte  ptr [rbx+r9], r8b

G_M000_IG69:                ;; offset=0x0A1F
       mov      r9d, esi
       inc      r11d
       mov      dword ptr [rbp+0x90], r10d
       mov      r15, bword ptr [rbp+0x08]

G_M000_IG70:                ;; offset=0x0A30
       mov      edi, dword ptr [rbp+0x40]
       cmp      r11d, edi
       mov      eax, dword ptr [rbp+0x7C]
       mov      r14, bword ptr [rbp]
       jl       G_M000_IG07

G_M000_IG71:                ;; offset=0x0A43
       mov      rax, rcx
       mov      r9, 0x4A607C0744FB
       cmp      qword ptr [rbp+0xA0], r9
       je       SHORT G_M000_IG72
       call     CORINFO_HELP_FAIL_FAST

G_M000_IG72:                ;; offset=0x0A5E
       nop

G_M000_IG73:                ;; offset=0x0A5F
       lea      rsp, [rbp+0xA8]
       pop      rbx
       pop      rsi
       pop      rdi
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret

G_M000_IG74:                ;; offset=0x0A73
       mov      rax, -1
       mov      r9, 0x4A607C0744FB
       cmp      qword ptr [rbp+0xA0], r9
       je       SHORT G_M000_IG75
       call     CORINFO_HELP_FAIL_FAST

G_M000_IG75:                ;; offset=0x0A95
       nop

G_M000_IG76:                ;; offset=0x0A96
       lea      rsp, [rbp+0xA8]
       pop      rbx
       pop      rsi
       pop      rdi
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret

G_M000_IG77:                ;; offset=0x0AAA
       call     CORINFO_HELP_RNGCHKFAIL
       int3

RWD00  	dq	4090000000000000h	;         1024
RWD08  	dq	43E0000000000000h

; Total bytes of code 2736
