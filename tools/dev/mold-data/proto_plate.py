LAST_R=None
# Prototype Mindlin MITC4 plate on a rectangular grid + Navier references.
# Convention: u=-z*bx, v=-z*by ; kx=dbx/dx, ky=dby/dy, kxy=dbx/dy+dby/dx ; gx=dw/dx-bx, gy=dw/dy-by
import numpy as np, math

def solve_plate(a, b, t, E, nu, q, nx, ny, edges, load_rect=None, kfound=0.0, line_supports=()):
    """a along x, b along y. edges: dict x0,xa,y0,yb -> 'S'(hard SS),'s'(soft SS),'C','F'.
    load_rect: (x1,x2,y1,y2) patch of pressure q, else full plate. kfound: Winkler modulus (pressure/deflection).
    Returns (w grid, Mx,My,Mxy nodal grids)."""
    D = E*t**3/(12*(1-nu**2)); G = E/(2*(1+nu)); ks = 5/6*G*t
    Db = D*np.array([[1,nu,0],[nu,1,0],[0,0,(1-nu)/2]])
    hx, hy = a/nx, b/ny
    nn = (nx+1)*(ny+1); ndof = 3*nn
    K = np.zeros((ndof, ndof)); F = np.zeros(ndof)
    xi_n = np.array([-1,1,1,-1]); et_n = np.array([-1,-1,1,1])
    def N(xi,et): return 0.25*(1+xi*xi_n)*(1+et*et_n)
    def dN(xi,et): return 0.25*xi_n*(1+et*et_n)*2/hx, 0.25*et_n*(1+xi*xi_n)*2/hy
    def Bb(xi,et):
        dx,dy = dN(xi,et); B = np.zeros((3,12))
        for i in range(4):
            B[0,3*i+1]=dx[i]; B[1,3*i+2]=dy[i]; B[2,3*i+1]=dy[i]; B[2,3*i+2]=dx[i]
        return B
    def Bgx(xi,et):
        dx,_=dN(xi,et); n=N(xi,et); r=np.zeros(12)
        for i in range(4): r[3*i]=dx[i]; r[3*i+1]=-n[i]
        return r
    def Bgy(xi,et):
        _,dy=dN(xi,et); n=N(xi,et); r=np.zeros(12)
        for i in range(4): r[3*i]=dy[i]; r[3*i+2]=-n[i]
        return r
    gA,gC = Bgx(0,-1),Bgx(0,1); gD,gB = Bgy(-1,0),Bgy(1,0)
    g = 1/math.sqrt(3); gp=[(-g,-g),(g,-g),(g,g),(-g,g)]
    detJ = hx*hy/4
    Ke = np.zeros((12,12)); Kf=np.zeros((12,12))
    for (xi,et) in gp:
        B=Bb(xi,et); Ke += B.T@Db@B*detJ
        Bs = np.vstack([(1-et)/2*gA+(1+et)/2*gC, (1-xi)/2*gD+(1+xi)/2*gB])
        Ke += ks*Bs.T@Bs*detJ
        n=N(xi,et); 
        for i in range(4):
            for j in range(4): Kf[3*i,3*j]+=kfound*n[i]*n[j]*detJ
    for ey in range(ny):
        for ex in range(nx):
            nodes=[ey*(nx+1)+ex, ey*(nx+1)+ex+1, (ey+1)*(nx+1)+ex+1, (ey+1)*(nx+1)+ex]
            dofs=np.array([[3*n,3*n+1,3*n+2] for n in nodes]).ravel()
            xc=(ex+0.5)*hx; yc=(ey+0.5)*hy
            Kel = Ke + (Kf if kfound else 0)
            K[np.ix_(dofs,dofs)] += Kel
            on = load_rect is None or (load_rect[0]<=xc<=load_rect[1] and load_rect[2]<=yc<=load_rect[3])
            if on:
                for i,n in enumerate(nodes): F[3*n] += q*hx*hy/4
    fixed=set()
    for j in range(ny+1):
        for i in range(nx+1):
            n=j*(nx+1)+i
            for key,cond in (('x0',i==0),('xa',i==nx),('y0',j==0),('yb',j==ny)):
                if not cond: continue
                bc=edges[key]
                if bc in 'SsC': fixed.add(3*n)
                if bc=='C': fixed.update([3*n+1,3*n+2])
                if bc=='S':  # tangential rotation fixed
                    fixed.add(3*n+2 if key in('x0','xa') else 3*n+1)
    for (axis,pos) in line_supports:  # rigid line support w=0 along x=pos or y=pos
        for j in range(ny+1):
            for i in range(nx+1):
                if axis=='x' and abs(i*hx-pos)<1e-9: fixed.add(3*(j*(nx+1)+i))
                if axis=='y' and abs(j*hy-pos)<1e-9: fixed.add(3*(j*(nx+1)+i))
    free=np.array(sorted(set(range(ndof))-fixed))
    U=np.zeros(ndof); U[free]=np.linalg.solve(K[np.ix_(free,free)],F[free])
    w=U[0::3].reshape(ny+1,nx+1)
    R=K@U-F
    global LAST_R; LAST_R=(R[0::3].reshape(ny+1,nx+1),R[1::3].reshape(ny+1,nx+1),R[2::3].reshape(ny+1,nx+1),hx,hy)
    # moments: gauss -> extrapolate to nodes, average
    Msum=np.zeros((nn,3)); cnt=np.zeros(nn)
    s=math.sqrt(3)
    for ey in range(ny):
        for ex in range(nx):
            nodes=[ey*(nx+1)+ex, ey*(nx+1)+ex+1, (ey+1)*(nx+1)+ex+1, (ey+1)*(nx+1)+ex]
            dofs=np.array([[3*n,3*n+1,3*n+2] for n in nodes]).ravel()
            Mg=np.array([Db@Bb(xi,et)@U[dofs] for (xi,et) in gp])
            for k,n in enumerate(nodes):
                Ne=0.25*(1+s*xi_n[k]*np.array([-1,1,1,-1]))*(1+s*et_n[k]*np.array([-1,-1,1,1]))
                Msum[n]+=Ne@Mg; cnt[n]+=1
    M=(Msum.T/cnt).T
    return w, M[:,0].reshape(ny+1,nx+1), M[:,1].reshape(ny+1,nx+1), M[:,2].reshape(ny+1,nx+1)

def navier_ss(a,b,t,E,nu,q,x,y,shear=True,terms=199):
    D=E*t**3/(12*(1-nu**2)); G=E/(2*(1+nu)); ks=5/6*G*t
    w=Mx=My=0.0
    for m in range(1,terms+1,2):
        for n in range(1,terms+1,2):
            am=m*math.pi/a; bn=n*math.pi/b; k2=am*am+bn*bn
            qmn=16*q/(math.pi**2*m*n); s=math.sin(am*x)*math.sin(bn*y)
            wk=qmn/(D*k2*k2)
            w+=(wk+(qmn/(ks*k2) if shear else 0))*s
            Mx+=D*(am*am+nu*bn*bn)*wk*s; My+=D*(bn*bn+nu*am*am)*wk*s
    return w,Mx,My
