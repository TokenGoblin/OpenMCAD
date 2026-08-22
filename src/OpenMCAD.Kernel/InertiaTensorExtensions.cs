using OpenMCAD.Math;

namespace OpenMCAD.Kernel;

/// <summary>Transformations of an inertia tensor.</summary>
public static class InertiaTensorExtensions
{
    /// <summary>
    /// Rotates the tensor into another frame.
    /// </summary>
    /// <param name="tensor">The tensor, expressed about the centroid in some local frame.</param>
    /// <param name="rotation">The rotation from that frame to the target frame.</param>
    /// <remarks>
    /// An inertia tensor is a rank-2 tensor, so it transforms as <c>R I Rᵀ</c> rather than by
    /// rotating three independent numbers. Rotating the diagonal alone is a tempting shortcut and
    /// is wrong for any body whose principal axes are not aligned with the frame — which is most
    /// of them once a placement has any rotation in it.
    /// </remarks>
    public static InertiaTensor RotatedBy(this InertiaTensor tensor, Quatd rotation)
    {
        // Columns of R are the images of the local axes.
        Vec3d cx = rotation.Rotate(Vec3d.UnitX);
        Vec3d cy = rotation.Rotate(Vec3d.UnitY);
        Vec3d cz = rotation.Rotate(Vec3d.UnitZ);

        // I in matrix form, with products of inertia negated per convention.
        double[,] i =
        {
            { tensor.Ixx, -tensor.Ixy, -tensor.Ixz },
            { -tensor.Ixy, tensor.Iyy, -tensor.Iyz },
            { -tensor.Ixz, -tensor.Iyz, tensor.Izz },
        };

        double[,] r =
        {
            { cx.X, cy.X, cz.X },
            { cx.Y, cy.Y, cz.Y },
            { cx.Z, cy.Z, cz.Z },
        };

        // result = R * I * Rᵀ
        double[,] ri = new double[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double sum = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    sum += r[row, k] * i[k, column];
                }

                ri[row, column] = sum;
            }
        }

        double[,] result = new double[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double sum = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    sum += ri[row, k] * r[column, k];
                }

                result[row, column] = sum;
            }
        }

        return new InertiaTensor(
            result[0, 0],
            result[1, 1],
            result[2, 2],
            -result[0, 1],
            -result[0, 2],
            -result[1, 2]);
    }

    /// <summary>
    /// Shifts the tensor from the centroid to a parallel axis system, by the parallel axis theorem.
    /// </summary>
    /// <param name="tensor">The tensor about the centroid.</param>
    /// <param name="mass">The body mass in kilograms.</param>
    /// <param name="offset">The vector from the centroid to the new origin.</param>
    public static InertiaTensor ShiftedFromCentroid(
        this InertiaTensor tensor,
        double mass,
        Vec3d offset)
        => new(
            tensor.Ixx + (mass * ((offset.Y * offset.Y) + (offset.Z * offset.Z))),
            tensor.Iyy + (mass * ((offset.X * offset.X) + (offset.Z * offset.Z))),
            tensor.Izz + (mass * ((offset.X * offset.X) + (offset.Y * offset.Y))),
            tensor.Ixy + (mass * offset.X * offset.Y),
            tensor.Ixz + (mass * offset.X * offset.Z),
            tensor.Iyz + (mass * offset.Y * offset.Z));

    /// <summary>Adds two tensors, which is valid when both are about the same origin.</summary>
    /// <param name="left">The first tensor.</param>
    /// <param name="right">The second tensor.</param>
    public static InertiaTensor Add(this InertiaTensor left, InertiaTensor right)
        => new(
            left.Ixx + right.Ixx,
            left.Iyy + right.Iyy,
            left.Izz + right.Izz,
            left.Ixy + right.Ixy,
            left.Ixz + right.Ixz,
            left.Iyz + right.Iyz);

    /// <summary>Scales a tensor, for subtracting a removed region.</summary>
    /// <param name="tensor">The tensor.</param>
    /// <param name="factor">The scale factor.</param>
    public static InertiaTensor Scale(this InertiaTensor tensor, double factor)
        => new(
            tensor.Ixx * factor,
            tensor.Iyy * factor,
            tensor.Izz * factor,
            tensor.Ixy * factor,
            tensor.Ixz * factor,
            tensor.Iyz * factor);
}
